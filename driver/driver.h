/*
  driver.h - grblHAL driver for STM32F401 (WeAct Black Pill), register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  Based on the structure of the grblHAL STM32F4xx driver by Terje Io,
  rewritten without the ST HAL: only CMSIS headers and registers.

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

#ifndef __DRIVER_H__
#define __DRIVER_H__

#include <stdbool.h>
#include <stdint.h>

#include "stm32f4xx.h"

#include "my_machine.h"

#define OPTS_POSTPROCESSING

#include "grbl/driver_opts.h"

/* ---------------------------------------------------------------------- */
/*  GPIO access                                                            */
/* ---------------------------------------------------------------------- */

// Read one input pin, 0 or 1.
#define DIGITAL_IN(port, pin) ((((port)->IDR) >> (pin)) & 1U)
// Set one output pin, atomic (BSRR).
#define DIGITAL_OUT(port, pin, on) { (port)->BSRR = (on) ? (1UL << (pin)) : (1UL << ((pin) + 16U)); }

// GPIO port index, 0 = GPIOA.
#define GPIO_INDEX(port) ((uint32_t)(((uint32_t)(port) - GPIOA_BASE) / (GPIOB_BASE - GPIOA_BASE)))

// Input read modes of the limit / step / direction port groups.
#define GPIO_SHIFT0   0
#define GPIO_SHIFT1   1
#define GPIO_SHIFT2   2
#define GPIO_SHIFT3   3
#define GPIO_SHIFT4   4
#define GPIO_SHIFT5   5
#define GPIO_SHIFT6   6
#define GPIO_SHIFT7   7
#define GPIO_SHIFT8   8
#define GPIO_SHIFT9   9
#define GPIO_SHIFT10 10
#define GPIO_SHIFT11 11
#define GPIO_SHIFT12 12
#define GPIO_SHIFT13 13
#define GPIO_MAP     14
#define GPIO_BITBAND 15

#ifndef CONTROL_ENABLE
#define CONTROL_ENABLE (CONTROL_HALT|CONTROL_FEED_HOLD|CONTROL_CYCLE_START)
#endif

#ifndef UART1_ENABLE
#define UART1_ENABLE 1
#endif
#ifndef UART2_ENABLE
#define UART2_ENABLE 1
#endif
#ifndef UART1_BAUD_RATE
#define UART1_BAUD_RATE BAUD_RATE
#endif
#ifndef UART2_BAUD_RATE
#define UART2_BAUD_RATE BAUD_RATE
#endif

#ifndef DEFAULT_LIMIT_SWITCHES_FITTED
#define DEFAULT_LIMIT_SWITCHES_FITTED 0
#endif

#ifdef BOARD_CNC3018_BLACKPILL
#include "boards/cnc3018_blackpill_map.h"
#else
#error "No board selected, see my_machine.h"
#endif

/* ---------------------------------------------------------------------- */
/*  Clocks and timers                                                      */
/* ---------------------------------------------------------------------- */

// The Black Pill F401 has a 25 MHz crystal. The PLL is set up for 84 MHz
// (48 MHz for USB). Without a working crystal the PLL runs from the
// internal 16 MHz RC oscillator (USB may then be unreliable).
#ifndef HSE_CLOCK_HZ
#define HSE_CLOCK_HZ    25000000UL
#endif
#define F_CPU           84000000UL
#define F_APB1          (F_CPU / 2)         // 42 MHz, APB1 timers run at 84 MHz
#define F_APB2          F_CPU               // 84 MHz

// Main stepper timer: TIM5 (32 bit), counts up at 84 MHz / 4 = 21 MHz.
#define STEPPER_TIMER               TIM5
#define STEPPER_TIMER_IRQn          TIM5_IRQn
#define STEPPER_TIMER_IRQHandler    TIM5_IRQHandler
#ifndef STEPPER_TIMER_DIV
#define STEPPER_TIMER_DIV           4
#endif

// Spindle PWM timer: TIM1 channel 1 on PA8 (AF1), clocked at 84 MHz.
#define SPINDLE_PWM_TIMER           TIM1
#define SPINDLE_PWM_AF              1

/* Interrupt priorities, 0 = highest (4 bits, all preemption). */
#define IRQ_PRIO_STEPPER    0   ///< Step timer.
#define IRQ_PRIO_SERIAL     1   ///< Host USB / UART.
#define IRQ_PRIO_INPUTS     2   ///< Limit and control inputs (EXTI).
#define IRQ_PRIO_SYSTICK    3   ///< 1 ms tick.

// Adjust these values to get more accurate step pulse timings when required,
// e.g if using high step rates. Values in microseconds.
// Minimum pulse off time.
#ifndef STEP_PULSE_TOFF_MIN
#define STEP_PULSE_TOFF_MIN 2.0f
#endif
// Time from step out to step reset.
#ifndef STEP_PULSE_TOFF_LATENCY
#define STEP_PULSE_TOFF_LATENCY 1.0f
#endif

#include "grbl/driver_opts2.h"

#ifndef STEP_PINMODE
#define STEP_PINMODE PINMODE_OUTPUT
#endif
#ifndef DIRECTION_PINMODE
#define DIRECTION_PINMODE PINMODE_OUTPUT
#endif
#ifndef STEPPERS_ENABLE_PINMODE
#define STEPPERS_ENABLE_PINMODE PINMODE_OUTPUT
#endif

/* ---------------------------------------------------------------------- */
/*  Pin descriptors                                                        */
/* ---------------------------------------------------------------------- */

typedef struct {
    pin_function_t id;
    pin_cap_t cap;
    pin_mode_t mode;
    uint8_t pin;
    uint32_t bit;
    GPIO_TypeDef *port;
    pin_group_t group;
    uint8_t user_port;
    volatile bool active;
    ioport_interrupt_callback_ptr interrupt_callback;
    const char *description;
} input_signal_t;

typedef struct {
    pin_function_t id;
    GPIO_TypeDef *port;
    uint8_t pin;
    pin_group_t group;
    pin_mode_t mode;
    const char *description;
} output_signal_t;

typedef struct {
    uint8_t n_pins;
    union {
        input_signal_t *inputs;
        output_signal_t *outputs;
    } pins;
} pin_group_pins_t;

/* GPIO configuration helpers (driver.c). */
typedef enum {
    GpioMode_Input = 0,
    GpioMode_Output = 1,
    GpioMode_Alternate = 2,
    GpioMode_Analog = 3
} gpio_mode_t;

void gpio_config (GPIO_TypeDef *port, uint8_t pin, gpio_mode_t mode, bool open_drain, pull_mode_t pull, uint8_t af);
void gpio_set_pull (GPIO_TypeDef *port, uint8_t pin, pull_mode_t pull);
void gpio_irq_enable (const input_signal_t *input, pin_irq_mode_t irq_mode);

bool driver_init (void);
void driver_systick (void);

/* ioports_aux.c */
void ioports_init (pin_group_pins_t *aux_inputs, pin_group_pins_t *aux_outputs);
void ioports_event (input_signal_t *input);

/* spindle.c */
void driver_spindles_init (void);
bool aux_out_claim_explicit (aux_ctrl_out_t *aux_ctrl);

/* nvs_flash.c */
bool memcpy_from_flash (uint8_t *dest);
bool memcpy_to_flash (uint8_t *source);

/* serial.c / usb_cdc.c / stream_mux.c */
#define N_UARTS 2       // USART1, USART2

void serialRegisterStreams (void);
const io_stream_t *serialOpen (uint8_t instance, uint32_t baud_rate);
const io_stream_t *usbInit (void);
bool stream_mux_connect (const io_stream_t *const *port, uint_fast8_t n);

/* cpu.c */
typedef enum {
    CpuClock_HSE = 0,   ///< PLL from the crystal, exact.
    CpuClock_HSI,       ///< Crystal failed, PLL from internal RC, +-1 %.
    CpuClock_Failed     ///< PLL failed, running at 16 MHz HSI, timing wrong!
} cpu_clock_t;

extern cpu_clock_t cpu_clock_source;

void cpu_init (void);
void cpu_check_bootloader (void);
void cpu_reboot_to_bootloader (void);
void delay_us (uint32_t us);

#endif // __DRIVER_H__
