/*
  serial.c - USART1 (PA9 TX / PA10 RX) and USART2 (PA2 TX / PA3 RX)
             streams for the STM32F401 Black Pill driver, register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).
  Follows the grblHAL STM32F4xx driver (Copyright (c) 2019-2025 Terje Io).

  Interrupt driven, 8N1, default 115200 baud. Stream instance 0 is USART1,
  instance 1 USART2. With USB_SERIAL_CDC = 0 USART1 is the host port; the
  enabled ports (UART1_ENABLE, UART2_ENABLE) work in parallel with it, see
  stream_mux.c. Ports not used by the driver stay claimable streams.

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

#define UART_AF 7               // USART1 and USART2 are AF7 on PA2/PA3/PA9/PA10

typedef struct {
    USART_TypeDef *uart;
    IRQn_Type irq;
    volatile uint32_t *rcc_enr;
    uint32_t rcc_en;
    uint32_t clock;
    GPIO_TypeDef *port;
    uint8_t tx_pin;
    uint8_t rx_pin;
} uart_hw_t;

typedef struct {
    const uart_hw_t *hw;
    stream_rx_buffer_t rxbuf;
    stream_tx_buffer_t txbuf;
    enqueue_realtime_command_ptr enqueue_realtime_command;
    io_stream_status_t status;
} uart_t;

static const uart_hw_t uart_hw[N_UARTS] = {
    {
        .uart = USART1,
        .irq = USART1_IRQn,
        .rcc_enr = &RCC->APB2ENR,
        .rcc_en = RCC_APB2ENR_USART1EN,
        .clock = F_APB2,
        .port = UART1_PORT,
        .tx_pin = UART1_TX_PIN,
        .rx_pin = UART1_RX_PIN
    },
    {
        .uart = USART2,
        .irq = USART2_IRQn,
        .rcc_enr = &RCC->APB1ENR,
        .rcc_en = RCC_APB1ENR_USART2EN,
        .clock = F_APB1,
        .port = UART2_PORT,
        .tx_pin = UART2_TX_PIN,
        .rx_pin = UART2_RX_PIN
    }
};

static uart_t uarts[N_UARTS];

/* ---------------------------------------------------------------------- */
/*  Port functions, for one UART                                          */
/* ---------------------------------------------------------------------- */

// Returns number of free characters in the input buffer
static uint16_t uart_rx_free (uart_t *u)
{
    uint_fast16_t tail = u->rxbuf.tail;
    uint_fast16_t head = u->rxbuf.head;

    return (uint16_t)((RX_BUFFER_SIZE - 1) - BUFCOUNT(head, tail, RX_BUFFER_SIZE));
}

// Returns number of characters in the input buffer
static uint16_t uart_rx_count (uart_t *u)
{
    uint_fast16_t tail = u->rxbuf.tail;
    uint_fast16_t head = u->rxbuf.head;

    return (uint16_t)BUFCOUNT(head, tail, RX_BUFFER_SIZE);
}

// Flushes the input buffer
static void uart_rx_flush (uart_t *u)
{
    u->rxbuf.tail = u->rxbuf.head;
}

// Flushes and adds a CAN character to the input buffer
static void uart_rx_cancel (uart_t *u)
{
    uint_fast16_t head = u->rxbuf.head;

    u->rxbuf.data[head] = ASCII_CAN;
    u->rxbuf.tail = head;
    u->rxbuf.head = BUFNEXT(head, u->rxbuf);
}

// Writes a character to the output stream, waits while the buffer is full
static bool uart_putc (uart_t *u, const uint8_t c)
{
    uint_fast16_t head = u->txbuf.head;
    uint_fast16_t next_head = BUFNEXT(head, u->txbuf);  // Next free slot in the buffer

    while(u->txbuf.tail == next_head) {                 // While TX buffer full
        if(!hal.stream_blocking_callback())             // check if blocking for space,
            return false;                               // exit if not (leaves TX buffer in an inconsistent state)
    }
    u->txbuf.data[head] = c;                            // Add data to buffer,
    u->txbuf.head = next_head;                          // update head pointer and
    u->hw->uart->CR1 |= USART_CR1_TXEIE;                // enable TX interrupts

    return true;
}

// Writes a null terminated string to the output stream, blocks if buffer full
static void uart_write_s (uart_t *u, const char *s)
{
    uint8_t c, *ptr = (uint8_t *)s;

    while((c = *ptr++) != '\0')
        uart_putc(u, c);
}

// Writes a number of characters to the output stream, blocks if buffer full
static void uart_write (uart_t *u, const uint8_t *s, uint16_t length)
{
    uint8_t *ptr = (uint8_t *)s;

    while(length--)
        uart_putc(u, *ptr++);
}

// Flushes the output buffer
static void uart_tx_flush (uart_t *u)
{
    u->hw->uart->CR1 &= ~USART_CR1_TXEIE;   // Disable TX interrupts
    u->txbuf.tail = u->txbuf.head;
}

// Returns number of characters pending transmission
static uint16_t uart_tx_count (uart_t *u)
{
    uint_fast16_t tail = u->txbuf.tail;
    uint_fast16_t head = u->txbuf.head;
    uint16_t sending = (u->hw->uart->SR & USART_SR_TC) ? 0 : 1;

    return (uint16_t)(BUFCOUNT(head, tail, TX_BUFFER_SIZE) + sending);
}

// Returns -1 if no data available
static int32_t uart_getc (uart_t *u)
{
    uint_fast16_t tail = u->rxbuf.tail;         // Get buffer pointer

    if(tail == u->rxbuf.head)
        return -1; // no data available

    int32_t data = (int32_t)u->rxbuf.data[tail]; // Get next character
    u->rxbuf.tail = BUFNEXT(tail, u->rxbuf);     // and update pointer

    return data;
}

static bool uart_suspend_input (uart_t *u, bool suspend)
{
    return stream_rx_suspend(&u->rxbuf, suspend);
}

static bool uart_set_baud_rate (uart_t *u, uint32_t baud_rate)
{
    USART_TypeDef *uart = u->hw->uart;

    u->status.baud_rate = baud_rate;

    uart->CR1 &= ~(USART_CR1_UE|USART_CR1_RXNEIE|USART_CR1_RE|USART_CR1_TE);
    uart->BRR = (u->hw->clock + baud_rate / 2U) / baud_rate;   // Oversampling by 16: mantissa and fraction.
    uart->CR1 |= (USART_CR1_RE|USART_CR1_TE|USART_CR1_UE|USART_CR1_RXNEIE);

    return true;
}

static bool uart_set_format (uart_t *u, serial_format_t format)
{
    USART_TypeDef *uart = u->hw->uart;

    u->status.format = format;

    uart->CR1 &= ~(USART_CR1_M|USART_CR1_PCE|USART_CR1_PS);

    if(format.parity != Serial_ParityNone)
        uart->CR1 |= (format.parity == Serial_ParityEven ? (USART_CR1_M|USART_CR1_PCE) : (USART_CR1_M|USART_CR1_PCE|USART_CR1_PS));

    uart->CR2 = (uart->CR2 & ~USART_CR2_STOP) | (format.stopbits == Serial_StopBits2 ? USART_CR2_STOP_1 : 0);

    return true;
}

static bool uart_disable_rx (uart_t *u, bool disable)
{
    if(disable)
        u->hw->uart->CR1 &= ~USART_CR1_RXNEIE;
    else
        u->hw->uart->CR1 |= USART_CR1_RXNEIE;

    return true;
}

static bool uart_enqueue_rt_command (uart_t *u, uint8_t c)
{
    return u->enqueue_realtime_command(c);
}

static enqueue_realtime_command_ptr uart_set_rt_handler (uart_t *u, enqueue_realtime_command_ptr handler)
{
    enqueue_realtime_command_ptr prev = u->enqueue_realtime_command;

    if(handler)
        u->enqueue_realtime_command = handler;

    return prev;
}

static void uart_irq (uart_t *u)
{
    USART_TypeDef *uart = u->hw->uart;
    uint32_t sr = uart->SR;

    if(sr & (USART_SR_RXNE|USART_SR_ORE)) {
        uint32_t data = uart->DR;                                   // Also clears ORE, FE and NE
        // A character with a framing or noise error is dropped: e.g. the
        // boot messages of an ESP8266 (74880 baud) are mostly such garbage.
        bool garbled = (sr & (USART_SR_FE|USART_SR_NE)) != 0;
        if(!garbled && !u->enqueue_realtime_command((uint8_t)data)) { // Check and strip realtime commands...
            uint_fast16_t head = u->rxbuf.head;
            uint_fast16_t next_head = BUFNEXT(head, u->rxbuf);      // Get and increment buffer pointer
            if(next_head == u->rxbuf.tail)                          // If buffer full
                u->rxbuf.overflow = 1;                              // flag overflow
            else {
                u->rxbuf.data[head] = (uint8_t)data;                // if not add data to buffer
                u->rxbuf.head = next_head;                          // and update pointer
            }
        }
    }

    if((sr & USART_SR_TXE) && (uart->CR1 & USART_CR1_TXEIE)) {
        uint_fast16_t tail = u->txbuf.tail;     // Get buffer pointer
        uart->DR = u->txbuf.data[tail];         // Send next character
        tail = BUFNEXT(tail, u->txbuf);         // and increment pointer
        u->txbuf.tail = tail;
        if(tail == u->txbuf.head)               // If buffer empty then
            uart->CR1 &= ~USART_CR1_TXEIE;      // disable UART TX interrupt
    }
}

// Claims a port: clocks, pins and interrupt the first time.
static bool uart_claim (uart_t *u, io_stream_properties_t *props)
{
    const uart_hw_t *hw = u->hw;

    if(!props->flags.claimable || props->flags.claimed)
        return false;

    props->flags.claimed = On;

    if(!props->flags.init_ok) {

        *hw->rcc_enr |= hw->rcc_en;
        (void)*hw->rcc_enr;

        gpio_config(hw->port, hw->tx_pin, GpioMode_Alternate, false, PullMode_None, UART_AF);
        gpio_config(hw->port, hw->rx_pin, GpioMode_Alternate, false, PullMode_Up, UART_AF);

        hw->uart->CR1 = 0;
        hw->uart->CR2 = 0;
        hw->uart->CR3 = 0;

        NVIC_SetPriority(hw->irq, IRQ_PRIO_SERIAL);
        NVIC_EnableIRQ(hw->irq);

        props->flags.init_ok = On;
    }

    return true;
}

/* ---------------------------------------------------------------------- */
/*  Streams: the handlers of io_stream_t take no instance, one set each   */
/* ---------------------------------------------------------------------- */

static const io_stream_t *uart1_init (uint32_t baud_rate);
static const io_stream_t *uart2_init (uint32_t baud_rate);
static bool uart_release (uint8_t instance);
static const io_stream_status_t *get_uart_status (uint8_t instance);

static io_stream_properties_t serial[N_UARTS] = {
    {
      .type = StreamType_Serial,
      .instance = 0,
      .flags.claimable = On,
      .flags.claimed = Off,
      .flags.can_set_baud = On,
      .flags.modbus_ready = On,
      .claim = uart1_init,
      .release = uart_release,
      .get_status = get_uart_status
    },
    {
      .type = StreamType_Serial,
      .instance = 1,
      .flags.claimable = On,
      .flags.claimed = Off,
      .flags.can_set_baud = On,
      .flags.modbus_ready = On,
      .claim = uart2_init,
      .release = uart_release,
      .get_status = get_uart_status
    }
};

#define UART_STREAM(n, idx) \
static uint16_t uart##n##_rx_free (void) { return uart_rx_free(&uarts[idx]); } \
static uint16_t uart##n##_rx_count (void) { return uart_rx_count(&uarts[idx]); } \
static void uart##n##_rx_flush (void) { uart_rx_flush(&uarts[idx]); } \
static void uart##n##_rx_cancel (void) { uart_rx_cancel(&uarts[idx]); } \
static bool uart##n##_putc (const uint8_t c) { return uart_putc(&uarts[idx], c); } \
static void uart##n##_write_s (const char *s) { uart_write_s(&uarts[idx], s); } \
static void uart##n##_write (const uint8_t *s, uint16_t length) { uart_write(&uarts[idx], s, length); } \
static void uart##n##_tx_flush (void) { uart_tx_flush(&uarts[idx]); } \
static uint16_t uart##n##_tx_count (void) { return uart_tx_count(&uarts[idx]); } \
static int32_t uart##n##_getc (void) { return uart_getc(&uarts[idx]); } \
static bool uart##n##_suspend_input (bool suspend) { return uart_suspend_input(&uarts[idx], suspend); } \
static bool uart##n##_set_baud_rate (uint32_t baud_rate) { return uart_set_baud_rate(&uarts[idx], baud_rate); } \
static bool uart##n##_set_format (serial_format_t format) { return uart_set_format(&uarts[idx], format); } \
static bool uart##n##_disable_rx (bool disable) { return uart_disable_rx(&uarts[idx], disable); } \
static bool uart##n##_enqueue_rt_command (uint8_t c) { return uart_enqueue_rt_command(&uarts[idx], c); } \
static enqueue_realtime_command_ptr uart##n##_set_rt_handler (enqueue_realtime_command_ptr handler) { return uart_set_rt_handler(&uarts[idx], handler); } \
\
static const io_stream_t *uart##n##_init (uint32_t baud_rate) \
{ \
    static const io_stream_t stream = { \
        .type = StreamType_Serial, \
        .instance = idx, \
        .is_connected = stream_connected, \
        .read = uart##n##_getc, \
        .write = uart##n##_write_s, \
        .write_n = uart##n##_write, \
        .write_char = uart##n##_putc, \
        .enqueue_rt_command = uart##n##_enqueue_rt_command, \
        .get_rx_buffer_free = uart##n##_rx_free, \
        .get_rx_buffer_count = uart##n##_rx_count, \
        .get_tx_buffer_count = uart##n##_tx_count, \
        .reset_write_buffer = uart##n##_tx_flush, \
        .reset_read_buffer = uart##n##_rx_flush, \
        .cancel_read_buffer = uart##n##_rx_cancel, \
        .suspend_read = uart##n##_suspend_input, \
        .disable_rx = uart##n##_disable_rx, \
        .set_baud_rate = uart##n##_set_baud_rate, \
        .set_format = uart##n##_set_format, \
        .set_enqueue_rt_handler = uart##n##_set_rt_handler \
    }; \
\
    if(!uart_claim(&uarts[idx], &serial[idx])) \
        return NULL; \
\
    stream_set_defaults(&stream, baud_rate); \
\
    return &stream; \
}

UART_STREAM(1, 0)
UART_STREAM(2, 1)

static const io_stream_status_t *get_uart_status (uint8_t instance)
{
    if(instance >= N_UARTS)
        return NULL;

    uarts[instance].status.flags = serial[instance].flags;

    return &uarts[instance].status;
}

static bool uart_release (uint8_t instance)
{
    bool ok;

    if((ok = instance < N_UARTS && serial[instance].flags.claimed))
        serial[instance].flags.claimed = Off;

    return ok;
}

// Opens a port for the driver: instance 0 is USART1, 1 USART2.
const io_stream_t *serialOpen (uint8_t instance, uint32_t baud_rate)
{
    return instance < N_UARTS ? serial[instance].claim(baud_rate) : NULL;
}

void serialRegisterStreams (void)
{
    static io_stream_details_t streams = {
        .n_streams = N_UARTS,
        .streams = serial,
    };

    static const periph_pin_t tx1 = {
        .function = Output_TX,
        .group = PinGroup_UART,
        .port  = UART1_PORT,
        .pin   = UART1_TX_PIN,
        .mode  = { .mask = PINMODE_OUTPUT }
    };

    static const periph_pin_t rx1 = {
        .function = Input_RX,
        .group = PinGroup_UART,
        .port = UART1_PORT,
        .pin = UART1_RX_PIN,
        .mode = { .mask = PINMODE_NONE }
    };

    static const periph_pin_t tx2 = {
        .function = Output_TX,
        .group = PinGroup_UART2,
        .port  = UART2_PORT,
        .pin   = UART2_TX_PIN,
        .mode  = { .mask = PINMODE_OUTPUT }
    };

    static const periph_pin_t rx2 = {
        .function = Input_RX,
        .group = PinGroup_UART2,
        .port = UART2_PORT,
        .pin = UART2_RX_PIN,
        .mode = { .mask = PINMODE_NONE }
    };

    uint_fast8_t idx;

    for(idx = 0; idx < N_UARTS; idx++) {
        uarts[idx].hw = &uart_hw[idx];
        uarts[idx].enqueue_realtime_command = protocol_enqueue_realtime_command;
        uarts[idx].status.baud_rate = 115200;
        uarts[idx].status.format.width = Serial_8bit;
        uarts[idx].status.format.stopbits = Serial_StopBits1;
        uarts[idx].status.format.parity = Serial_ParityNone;
    }

    hal.periph_port.register_pin(&rx1);
    hal.periph_port.register_pin(&tx1);
    hal.periph_port.register_pin(&rx2);
    hal.periph_port.register_pin(&tx2);

    stream_register_streams(&streams);
}

void USART1_IRQHandler (void);
void USART1_IRQHandler (void)
{
    uart_irq(&uarts[0]);
}

void USART2_IRQHandler (void);
void USART2_IRQHandler (void)
{
    uart_irq(&uarts[1]);
}
