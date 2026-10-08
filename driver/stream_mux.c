/*
  stream_mux.c - several host ports at once: USB CDC and the UARTs (for
                 example a WiFi module such as an ESP-01 with esp-link or
                 ESP3D, or a Bluetooth module).

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  grblHAL reads G-code from one stream. This stream stands in front of up
  to three ports:

  - Input comes from the active port. Another port becomes the active one
    when it has sent something while the active port has been quiet for
    MUX_SWITCH_QUIET_MS at the end of a line and the machine stands still
    (idle, alarm or check mode, planner empty). Until then its characters
    wait in its own buffer and its sender waits for the "ok"s, as with a
    busy controller: nothing is lost or mixed into a running job.
    The new port starts clean: an ASCII CAN goes to the core first, which
    drops a line the old port left unfinished and clears grblHAL's
    "G-code locked after an error" state of the old port's sender, with
    no answer.
  - Real time commands (status report ?, feed hold !, cycle start ~,
    soft reset Ctrl-X, jog cancel, overrides) act from every port.
  - Line answers (ok, error:n, $ listings) go to the active port; status
    reports, alarms and messages (the core's write_all) to every connected
    port, so a phone on the WiFi port sees the machine while the computer
    runs a job over USB.

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  grblHAL is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with grblHAL. If not, see <http://www.gnu.org/licenses/>.
*/

#include "driver.h"

#include "grbl/hal.h"
#include "grbl/protocol.h"
#include "grbl/planner.h"
#include "grbl/state_machine.h"

#define MUX_MAX_PORTS           3
#define MUX_SWITCH_QUIET_MS     500     // The active port is quiet this long before another takes over.
#define MUX_LINE_TIMEOUT_MS     2000    // A line the active port left unfinished is dropped after this.

static const io_stream_t *ports[MUX_MAX_PORTS];
static uint_fast8_t n_ports;
static volatile uint_fast8_t active;
static enqueue_realtime_command_ptr rt_handler = protocol_enqueue_realtime_command;
static uint32_t last_rx;                // Time of the last character read from the active port, ms.
static bool at_eol = true;              // The last character read ended a line.
static on_stream_changed_ptr on_stream_changed;

static io_stream_t mux;

/* ---------------------------------------------------------------------- */
/*  Real time commands from every port                                    */
/* ---------------------------------------------------------------------- */

// The active port uses the handler the core set (it may buffer everything,
// e.g. while a file is streamed); the others act on real time commands and
// keep the rest in their buffers for later.
static bool rt_port (uint_fast8_t idx, uint8_t c)
{
    return idx == active ? rt_handler(c) : protocol_enqueue_realtime_command(c);
}

static bool rt_port0 (uint8_t c) { return rt_port(0, c); }
static bool rt_port1 (uint8_t c) { return rt_port(1, c); }
static bool rt_port2 (uint8_t c) { return rt_port(2, c); }

static const enqueue_realtime_command_ptr rt_ports[MUX_MAX_PORTS] = { rt_port0, rt_port1, rt_port2 };

static enqueue_realtime_command_ptr mux_set_rt_handler (enqueue_realtime_command_ptr handler)
{
    enqueue_realtime_command_ptr prev = rt_handler;

    if(handler)
        rt_handler = handler;

    return prev;
}

static bool mux_enqueue_rt_command (uint8_t c)
{
    return rt_handler(c);
}

/* ---------------------------------------------------------------------- */
/*  Input                                                                 */
/* ---------------------------------------------------------------------- */

// Another port may take over only when nothing runs that its input could
// mix into.
static bool machine_is_still (void)
{
    sys_state_t state = state_get();

    return (state == STATE_IDLE || (state & (STATE_ALARM|STATE_ESTOP|STATE_CHECK_MODE))) &&
            plan_get_current_block() == NULL;
}

static int32_t mux_read (void)
{
    const io_stream_t *port = ports[active];
    int32_t c = port->read();

    if(c != SERIAL_NO_DATA) {
        last_rx = hal.get_elapsed_ticks();
        at_eol = c == ASCII_LF || c == ASCII_CR || c == ASCII_CAN;
        return c;
    }

    uint32_t quiet = hal.get_elapsed_ticks() - last_rx;
    uint_fast8_t idx = active;

    // A sender that stopped in the middle of a line loses it after a while.
    if(quiet < (at_eol ? MUX_SWITCH_QUIET_MS : MUX_LINE_TIMEOUT_MS))
        return SERIAL_NO_DATA;

    while((idx = (idx + 1) % n_ports) != active) {
        if(ports[idx]->get_rx_buffer_count() && machine_is_still()) {
            active = idx;
            at_eol = true;
            last_rx = hal.get_elapsed_ticks();
            return ASCII_CAN;               // Clean start for the new port, see above.
        }
    }

    return SERIAL_NO_DATA;
}

static uint16_t mux_rx_free (void)
{
    return ports[active]->get_rx_buffer_free();
}

static uint16_t mux_rx_count (void)
{
    const io_stream_t *port = ports[active];

    return port->get_rx_buffer_count ? port->get_rx_buffer_count() : 0;
}

static void mux_rx_flush (void)
{
    ports[active]->reset_read_buffer();
}

static void mux_rx_cancel (void)
{
    ports[active]->cancel_read_buffer();
}

static bool mux_suspend_input (bool suspend)
{
    const io_stream_t *port = ports[active];

    return port->suspend_read ? port->suspend_read(suspend) : false;
}

static bool mux_disable_rx (bool disable)
{
    uint_fast8_t idx;

    for(idx = 0; idx < n_ports; idx++) {
        if(ports[idx]->disable_rx)
            ports[idx]->disable_rx(disable);
    }

    return true;
}

/* ---------------------------------------------------------------------- */
/*  Output                                                                */
/* ---------------------------------------------------------------------- */

static void mux_write (const char *s)
{
    ports[active]->write(s);
}

static void mux_write_n (const uint8_t *s, uint16_t length)
{
    ports[active]->write_n(s, length);
}

static bool mux_write_char (const uint8_t c)
{
    return ports[active]->write_char(c);
}

// Status reports, alarms, messages: to every port that is connected (USB:
// a program has the port open).
static void mux_write_all (const char *s)
{
    uint_fast8_t idx;

    for(idx = 0; idx < n_ports; idx++) {
        if(ports[idx]->is_connected == NULL || ports[idx]->is_connected())
            ports[idx]->write(s);
    }
}

static uint16_t mux_tx_count (void)
{
    const io_stream_t *port = ports[active];

    return port->get_tx_buffer_count ? port->get_tx_buffer_count() : 0;
}

static void mux_tx_flush (void)
{
    const io_stream_t *port = ports[active];

    if(port->reset_write_buffer)
        port->reset_write_buffer();
}

static bool mux_is_connected (void)
{
    return true;
}

// The core sets its own write_all handler on every stream change: put
// ours back while this stream is the current one.
static void onStreamChanged (void)
{
    if(hal.stream.read == mux_read)
        hal.stream.write_all = mux_write_all;

    if(on_stream_changed)
        on_stream_changed();
}

/* ---------------------------------------------------------------------- */
/*  Connect                                                               */
/* ---------------------------------------------------------------------- */

// Connects the ports as the host stream, the first one is active at start.
bool stream_mux_connect (const io_stream_t *const *port, uint_fast8_t n)
{
    uint_fast8_t idx;

    n_ports = 0;

    for(idx = 0; idx < n && n_ports < MUX_MAX_PORTS; idx++) {
        if(port[idx])
            ports[n_ports++] = port[idx];
    }

    if(n_ports == 0)
        return false;

    if(n_ports == 1)
        return stream_connect(ports[0]);

    for(idx = 0; idx < n_ports; idx++)
        ports[idx]->set_enqueue_rt_handler(rt_ports[idx]);

    mux.type = ports[0]->type;
    mux.instance = ports[0]->instance;
    mux.state.is_usb = ports[0]->state.is_usb;
    mux.is_connected = mux_is_connected;
    mux.read = mux_read;
    mux.write = mux_write;
    mux.write_n = mux_write_n;
    mux.write_char = mux_write_char;
    mux.enqueue_rt_command = mux_enqueue_rt_command;
    mux.get_rx_buffer_free = mux_rx_free;
    mux.get_rx_buffer_count = mux_rx_count;
    mux.get_tx_buffer_count = mux_tx_count;
    mux.reset_write_buffer = mux_tx_flush;
    mux.reset_read_buffer = mux_rx_flush;
    mux.cancel_read_buffer = mux_rx_cancel;
    mux.suspend_read = mux_suspend_input;
    mux.disable_rx = mux_disable_rx;
    mux.set_enqueue_rt_handler = mux_set_rt_handler;

    active = 0;
    last_rx = hal.get_elapsed_ticks();

    on_stream_changed = grbl.on_stream_changed;
    grbl.on_stream_changed = onStreamChanged;

    if(!stream_connect(&mux))
        return false;

    hal.stream.write_all = mux_write_all;

    return true;
}
