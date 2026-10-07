/*
  serial.c - USART1 stream (PA9 TX / PA10 RX) for the STM32F401 Black Pill
             driver, register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).
  Follows the grblHAL STM32F4xx driver (Copyright (c) 2019-2025 Terje Io).

  Interrupt driven, 8N1, default 115200 baud. With USB_SERIAL_CDC = 0 this
  is the host port, otherwise it is registered as a claimable stream
  (e.g. for an MPG / pendant or a Bluetooth module).

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

#define UART            USART1
#define UART_IRQn       USART1_IRQn
#define UART_CLOCK      F_APB2              // USART1 is on APB2
#define UART_AF         7

static stream_rx_buffer_t rxbuf = {0};
static stream_tx_buffer_t txbuf = {0};
static enqueue_realtime_command_ptr enqueue_realtime_command = protocol_enqueue_realtime_command;

static const io_stream_t *serialInit (uint32_t baud_rate);

static io_stream_status_t stream_status = {
    .baud_rate = 115200,
    .format = {
        .width = Serial_8bit,
        .stopbits = Serial_StopBits1,
        .parity = Serial_ParityNone,
    }
};

static bool uart_release (uint8_t instance);
static const io_stream_status_t *get_uart_status (uint8_t instance);

static io_stream_properties_t serial[] = {
    {
      .type = StreamType_Serial,
      .instance = 0,
      .flags.claimable = On,
      .flags.claimed = Off,
      .flags.can_set_baud = On,
      .flags.modbus_ready = On,
      .claim = serialInit,
      .release = uart_release,
      .get_status = get_uart_status
    }
};

static const io_stream_status_t *get_uart_status (uint8_t instance)
{
    (void)instance;

    stream_status.flags = serial[0].flags;

    return &stream_status;
}

static bool uart_release (uint8_t instance)
{
    bool ok;

    (void)instance;

    if((ok = serial[0].flags.claimed))
        serial[0].flags.claimed = Off;

    return ok;
}

void serialRegisterStreams (void)
{
    static io_stream_details_t streams = {
        .n_streams = sizeof(serial) / sizeof(io_stream_properties_t),
        .streams = serial,
    };

    static const periph_pin_t tx = {
        .function = Output_TX,
        .group = PinGroup_UART,
        .port  = UART_PORT,
        .pin   = UART_TX_PIN,
        .mode  = { .mask = PINMODE_OUTPUT }
    };

    static const periph_pin_t rx = {
        .function = Input_RX,
        .group = PinGroup_UART,
        .port = UART_PORT,
        .pin = UART_RX_PIN,
        .mode = { .mask = PINMODE_NONE }
    };

    hal.periph_port.register_pin(&rx);
    hal.periph_port.register_pin(&tx);

    stream_register_streams(&streams);
}

// Returns number of free characters in serial input buffer
static uint16_t serialRxFree (void)
{
    uint_fast16_t tail = rxbuf.tail, head = rxbuf.head;

    return (uint16_t)((RX_BUFFER_SIZE - 1) - BUFCOUNT(head, tail, RX_BUFFER_SIZE));
}

// Returns number of characters in serial input buffer
static uint16_t serialRxCount (void)
{
    uint_fast16_t tail = rxbuf.tail, head = rxbuf.head;

    return (uint16_t)BUFCOUNT(head, tail, RX_BUFFER_SIZE);
}

// Flushes the serial input buffer
static void serialRxFlush (void)
{
    rxbuf.tail = rxbuf.head;
}

// Flushes and adds a CAN character to the serial input buffer
static void serialRxCancel (void)
{
    rxbuf.data[rxbuf.head] = ASCII_CAN;
    rxbuf.tail = rxbuf.head;
    rxbuf.head = BUFNEXT(rxbuf.head, rxbuf);
}

// Writes a character to the serial output stream
static bool serialPutC (const uint8_t c)
{
    uint_fast16_t next_head = BUFNEXT(txbuf.head, txbuf);   // Get pointer to next free slot in buffer

    while(txbuf.tail == next_head) {                    // While TX buffer full
        if(!hal.stream_blocking_callback())             // check if blocking for space,
            return false;                               // exit if not (leaves TX buffer in an inconsistent state)
    }
    txbuf.data[txbuf.head] = c;                         // Add data to buffer,
    txbuf.head = next_head;                             // update head pointer and
    UART->CR1 |= USART_CR1_TXEIE;                       // enable TX interrupts

    return true;
}

// Writes a null terminated string to the serial output stream, blocks if buffer full
static void serialWriteS (const char *s)
{
    uint8_t c, *ptr = (uint8_t *)s;

    while((c = *ptr++) != '\0')
        serialPutC(c);
}

// Writes a number of characters from string to the serial output stream, blocks if buffer full
static void serialWrite (const uint8_t *s, uint16_t length)
{
    uint8_t *ptr = (uint8_t *)s;

    while(length--)
        serialPutC(*ptr++);
}

// Flushes the serial output buffer
static void serialTxFlush (void)
{
    UART->CR1 &= ~USART_CR1_TXEIE;     // Disable TX interrupts
    txbuf.tail = txbuf.head;
}

// Returns number of characters pending transmission
static uint16_t serialTxCount (void)
{
    uint_fast16_t tail = txbuf.tail, head = txbuf.head;

    return (uint16_t)(BUFCOUNT(head, tail, TX_BUFFER_SIZE) + ((UART->SR & USART_SR_TC) ? 0 : 1));
}

// Returns -1 if no data available
static int32_t serialGetC (void)
{
    uint_fast16_t tail = rxbuf.tail;            // Get buffer pointer

    if(tail == rxbuf.head)
        return -1; // no data available

    int32_t data = (int32_t)rxbuf.data[tail];   // Get next character
    rxbuf.tail = BUFNEXT(tail, rxbuf);          // and update pointer

    return data;
}

static bool serialSuspendInput (bool suspend)
{
    return stream_rx_suspend(&rxbuf, suspend);
}

// Baud rate register for oversampling by 16: mantissa and fraction.
static uint32_t uart_brr (uint32_t clock, uint32_t baud)
{
    return (clock + baud / 2U) / baud;
}

static bool serialSetBaudRate (uint32_t baud_rate)
{
    stream_status.baud_rate = baud_rate;

    UART->CR1 &= ~(USART_CR1_UE|USART_CR1_RXNEIE|USART_CR1_RE|USART_CR1_TE);
    UART->BRR = uart_brr(UART_CLOCK, baud_rate);
    UART->CR1 |= (USART_CR1_RE|USART_CR1_TE|USART_CR1_UE|USART_CR1_RXNEIE);

    return true;
}

static bool serialSetFormat (serial_format_t format)
{
    stream_status.format = format;

    UART->CR1 &= ~(USART_CR1_M|USART_CR1_PCE|USART_CR1_PS);

    if(format.parity != Serial_ParityNone)
        UART->CR1 |= (format.parity == Serial_ParityEven ? (USART_CR1_M|USART_CR1_PCE) : (USART_CR1_M|USART_CR1_PCE|USART_CR1_PS));

    UART->CR2 = (UART->CR2 & ~USART_CR2_STOP) | (format.stopbits == Serial_StopBits2 ? USART_CR2_STOP_1 : 0);

    return true;
}

static bool serialDisable (bool disable)
{
    if(disable)
        UART->CR1 &= ~USART_CR1_RXNEIE;
    else
        UART->CR1 |= USART_CR1_RXNEIE;

    return true;
}

static bool serialEnqueueRtCommand (uint8_t c)
{
    return enqueue_realtime_command(c);
}

static enqueue_realtime_command_ptr serialSetRtHandler (enqueue_realtime_command_ptr handler)
{
    enqueue_realtime_command_ptr prev = enqueue_realtime_command;

    if(handler)
        enqueue_realtime_command = handler;

    return prev;
}

static const io_stream_t *serialInit (uint32_t baud_rate)
{
    static const io_stream_t stream = {
        .type = StreamType_Serial,
        .instance = 0,
        .is_connected = stream_connected,
        .read = serialGetC,
        .write = serialWriteS,
        .write_n =  serialWrite,
        .write_char = serialPutC,
        .enqueue_rt_command = serialEnqueueRtCommand,
        .get_rx_buffer_free = serialRxFree,
        .get_rx_buffer_count = serialRxCount,
        .get_tx_buffer_count = serialTxCount,
        .reset_write_buffer = serialTxFlush,
        .reset_read_buffer = serialRxFlush,
        .cancel_read_buffer = serialRxCancel,
        .suspend_read = serialSuspendInput,
        .disable_rx = serialDisable,
        .set_baud_rate = serialSetBaudRate,
        .set_format = serialSetFormat,
        .set_enqueue_rt_handler = serialSetRtHandler
    };

    if(!serial[0].flags.claimable || serial[0].flags.claimed)
        return NULL;

    serial[0].flags.claimed = On;

    if(!serial[0].flags.init_ok) {

        RCC->APB2ENR |= RCC_APB2ENR_USART1EN;
        (void)RCC->APB2ENR;

        gpio_config(UART_PORT, UART_TX_PIN, GpioMode_Alternate, false, PullMode_None, UART_AF);
        gpio_config(UART_PORT, UART_RX_PIN, GpioMode_Alternate, false, PullMode_Up, UART_AF);

        UART->CR1 = 0;
        UART->CR2 = 0;
        UART->CR3 = 0;

        NVIC_SetPriority(UART_IRQn, IRQ_PRIO_SERIAL);
        NVIC_EnableIRQ(UART_IRQn);

        serial[0].flags.init_ok = On;
    }

    stream_set_defaults(&stream, baud_rate);

    return &stream;
}

void USART1_IRQHandler (void);
void USART1_IRQHandler (void)
{
    uint32_t sr = UART->SR;

    if(sr & (USART_SR_RXNE|USART_SR_ORE)) {
        uint32_t data = UART->DR;                               // Also clears ORE
        if(!enqueue_realtime_command((uint8_t)data)) {          // Check and strip realtime commands...
            uint_fast16_t next_head = BUFNEXT(rxbuf.head, rxbuf);   // Get and increment buffer pointer
            if(next_head == rxbuf.tail)                         // If buffer full
                rxbuf.overflow = 1;                             // flag overflow
            else {
                rxbuf.data[rxbuf.head] = (uint8_t)data;         // if not add data to buffer
                rxbuf.head = next_head;                         // and update pointer
            }
        }
    }

    if((sr & USART_SR_TXE) && (UART->CR1 & USART_CR1_TXEIE)) {
        uint_fast16_t tail = txbuf.tail;            // Get buffer pointer
        UART->DR = txbuf.data[tail];                // Send next character
        txbuf.tail = tail = BUFNEXT(tail, txbuf);   // and increment pointer
        if(tail == txbuf.head)                      // If buffer empty then
            UART->CR1 &= ~USART_CR1_TXEIE;          // disable UART TX interrupt
    }
}
