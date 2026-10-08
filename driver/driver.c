/*
  driver.c - grblHAL driver for STM32F401 (WeAct Black Pill), register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  Structure and timing logic follow the grblHAL STM32F4xx driver
  (Copyright (c) 2019-2026 Terje Io), rewritten for one board and without
  the ST HAL / LL libraries: only CMSIS device headers and registers.

  Resources:
    TIM5        main stepper timer (32 bit, down counting, 21 MHz)
    TIM1 CH1    spindle PWM on PA8 (spindle.c)
    SysTick     1 ms tick, delays, debounce tasks
    DWT CYCCNT  microsecond time base
    EXTI        limit switches, control inputs
    OTG_FS      USB CDC host port (usb_cdc.c) or USART1 (serial.c)
    Flash       sector 1 holds the settings (nvs_flash.c)

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

#include <math.h>
#include <string.h>
#include <stdlib.h>

#include "driver.h"

#include "grbl/task.h"
#include "grbl/motor_pins.h"
#include "grbl/pin_bits_masks.h"
#include "grbl/state_machine.h"
#include "grbl/machine_limits.h"
#include "grbl/protocol.h"
#include "grbl/report.h"
#include "grbl/system.h"

#if (LIMIT_MASK|CONTROL_MASK|DEVICES_IRQ_MASK) != (LIMIT_MASK_SUM+CONTROL_MASK_SUM+DEVICES_IRQ_MASK_SUM)
#error Interrupt enabled input pins must have unique pin numbers!
#endif

#if STEP_OUTMODE != GPIO_MAP || DIRECTION_OUTMODE != GPIO_MAP
#error This driver only supports GPIO_MAP step and direction outputs!
#endif

#define DRIVER_IRQMASK (LIMIT_MASK|DEVICES_IRQ_MASK)

/* ---------------------------------------------------------------------- */
/*  Pin tables                                                             */
/* ---------------------------------------------------------------------- */

static input_signal_t inputpin[] = {
// Limit input pins must be consecutive in this array
    { .id = Input_LimitX,         .port = X_LIMIT_PORT,       .pin = X_LIMIT_PIN,         .group = PinGroup_Limit },
    { .id = Input_LimitY,         .port = Y_LIMIT_PORT,       .pin = Y_LIMIT_PIN,         .group = PinGroup_Limit },
    { .id = Input_LimitZ,         .port = Z_LIMIT_PORT,       .pin = Z_LIMIT_PIN,         .group = PinGroup_Limit },
// Aux input pins must be consecutive in this array
#ifdef AUXINPUT0_PIN
    { .id = Input_Aux0,           .port = AUXINPUT0_PORT,     .pin = AUXINPUT0_PIN,       .group = PinGroup_AuxInput },
#endif
#ifdef AUXINPUT1_PIN
    { .id = Input_Aux1,           .port = AUXINPUT1_PORT,     .pin = AUXINPUT1_PIN,       .group = PinGroup_AuxInput },
#endif
#ifdef AUXINPUT2_PIN
    { .id = Input_Aux2,           .port = AUXINPUT2_PORT,     .pin = AUXINPUT2_PIN,       .group = PinGroup_AuxInput },
#endif
#ifdef AUXINPUT3_PIN
    { .id = Input_Aux3,           .port = AUXINPUT3_PORT,     .pin = AUXINPUT3_PIN,       .group = PinGroup_AuxInput },
#endif
#ifdef AUXINPUT4_PIN
    { .id = Input_Aux4,           .port = AUXINPUT4_PORT,     .pin = AUXINPUT4_PIN,       .group = PinGroup_AuxInput },
#endif
};

static output_signal_t outputpin[] = {
    { .id = Output_StepX,           .port = X_STEP_PORT,            .pin = X_STEP_PIN,              .group = PinGroup_StepperStep,   .mode = {STEP_PINMODE} },
    { .id = Output_StepY,           .port = Y_STEP_PORT,            .pin = Y_STEP_PIN,              .group = PinGroup_StepperStep,   .mode = {STEP_PINMODE} },
    { .id = Output_StepZ,           .port = Z_STEP_PORT,            .pin = Z_STEP_PIN,              .group = PinGroup_StepperStep,   .mode = {STEP_PINMODE} },
    { .id = Output_DirX,            .port = X_DIRECTION_PORT,       .pin = X_DIRECTION_PIN,         .group = PinGroup_StepperDir,    .mode = {DIRECTION_PINMODE} },
    { .id = Output_DirY,            .port = Y_DIRECTION_PORT,       .pin = Y_DIRECTION_PIN,         .group = PinGroup_StepperDir,    .mode = {DIRECTION_PINMODE} },
    { .id = Output_DirZ,            .port = Z_DIRECTION_PORT,       .pin = Z_DIRECTION_PIN,         .group = PinGroup_StepperDir,    .mode = {DIRECTION_PINMODE} },
    { .id = Output_StepperEnable,   .port = STEPPERS_ENABLE_PORT,   .pin = STEPPERS_ENABLE_PIN,     .group = PinGroup_StepperEnable, .mode = {STEPPERS_ENABLE_PINMODE} },
#ifdef LED_PORT
    { .id = Output_LED,             .port = LED_PORT,               .pin = LED_PIN,                 .group = PinGroup_LED },
#endif
#ifdef AUXOUTPUT0_PORT
    { .id = Output_Aux0,            .port = AUXOUTPUT0_PORT,        .pin = AUXOUTPUT0_PIN,          .group = PinGroup_AuxOutput },
#endif
#ifdef AUXOUTPUT1_PORT
    { .id = Output_Aux1,            .port = AUXOUTPUT1_PORT,        .pin = AUXOUTPUT1_PIN,          .group = PinGroup_AuxOutput },
#endif
#ifdef AUXOUTPUT2_PORT
    { .id = Output_Aux2,            .port = AUXOUTPUT2_PORT,        .pin = AUXOUTPUT2_PIN,          .group = PinGroup_AuxOutput },
#endif
#ifdef AUXOUTPUT3_PORT
    { .id = Output_Aux3,            .port = AUXOUTPUT3_PORT,        .pin = AUXOUTPUT3_PIN,          .group = PinGroup_AuxOutput },
#endif
#ifdef AUXOUTPUT4_PORT
    { .id = Output_Aux4,            .port = AUXOUTPUT4_PORT,        .pin = AUXOUTPUT4_PIN,          .group = PinGroup_AuxOutput },
#endif
};

#define N_INPUTS  (sizeof(inputpin) / sizeof(input_signal_t))
#define N_OUTPUTS (sizeof(outputpin) / sizeof(output_signal_t))

/* ---------------------------------------------------------------------- */
/*  State                                                                  */
/* ---------------------------------------------------------------------- */

static volatile uint32_t tick_ms = 0;           // 1 ms tick counter
static volatile uint32_t tick_cycles = 0;       // DWT->CYCCNT at the last tick
static uint32_t systick_safe_read = 0, cycles2us_factor = 0;
static uint32_t aux_irq = 0;                    // EXTI lines of aux inputs
static bool IOInitDone = false;
static pin_group_pins_t limit_inputs = {0};
static delay_t delay = { .ms = 1, .callback = NULL }; // NOTE: initial ms set to 1 for "resetting" systick timer on startup
static input_signal_t *pin_irq[16] = {0};       // EXTI line -> input
static periph_signal_t *periph_pins = NULL;

// Step and direction outputs: BSRR words indexed by the axis bits.
static uint32_t step_bsrr[8], dir_bsrr[8];

static struct {
    // t_* parameters are timer ticks
    uint32_t t_min_period;
    uint32_t t_on;      // delayed pulse
    uint32_t t_off;
    axes_signals_t out;
} step_pulse = {0};

#if SAFETY_DOOR_ENABLE
static pin_debounce_t debounce;
#endif

static void aux_irq_handler (uint8_t port, bool state);

/* ---------------------------------------------------------------------- */
/*  GPIO helpers                                                           */
/* ---------------------------------------------------------------------- */

void gpio_set_pull (GPIO_TypeDef *port, uint8_t pin, pull_mode_t pull)
{
    uint32_t pupd = pull == PullMode_Up ? 1UL : (pull == PullMode_Down ? 2UL : 0UL);

    port->PUPDR = (port->PUPDR & ~(3UL << (pin * 2U))) | (pupd << (pin * 2U));
}

void gpio_config (GPIO_TypeDef *port, uint8_t pin, gpio_mode_t mode, bool open_drain, pull_mode_t pull, uint8_t af)
{
    uint32_t afr_shift = (pin & 7U) * 4U;

    __disable_irq();

    if(mode == GpioMode_Alternate)
        port->AFR[pin >> 3] = (port->AFR[pin >> 3] & ~(0xFUL << afr_shift)) | ((uint32_t)af << afr_shift);

    if(open_drain)
        port->OTYPER |= (1UL << pin);
    else
        port->OTYPER &= ~(1UL << pin);

    // Very high speed for outputs: sharp step edges.
    port->OSPEEDR |= (3UL << (pin * 2U));
    gpio_set_pull(port, pin, pull);
    port->MODER = (port->MODER & ~(3UL << (pin * 2U))) | ((uint32_t)mode << (pin * 2U));

    __enable_irq();
}

// Map EXTI line <pin> to the GPIO port of the input.
static void exti_map (const input_signal_t *input)
{
    uint32_t shift = (input->pin & 3U) * 4U;

    SYSCFG->EXTICR[input->pin >> 2] = (SYSCFG->EXTICR[input->pin >> 2] & ~(0xFUL << shift)) |
                                       (GPIO_INDEX(input->port) << shift);
}

void gpio_irq_enable (const input_signal_t *input, pin_irq_mode_t irq_mode)
{
    if(irq_mode == IRQ_Mode_Rising) {
        EXTI->RTSR |= input->bit;
        EXTI->FTSR &= ~input->bit;
    } else if(irq_mode == IRQ_Mode_Falling) {
        EXTI->RTSR &= ~input->bit;
        EXTI->FTSR |= input->bit;
    } else if(irq_mode == IRQ_Mode_Change) {
        EXTI->RTSR |= input->bit;
        EXTI->FTSR |= input->bit;
    } else
        EXTI->IMR &= ~input->bit;   // Disable pin interrupt

    if(irq_mode != IRQ_Mode_None)
        EXTI->IMR |= input->bit;    // Enable pin interrupt
}

/* ---------------------------------------------------------------------- */
/*  Delay, time                                                            */
/* ---------------------------------------------------------------------- */

static void driver_delay (uint32_t ms, delay_callback_ptr callback)
{
    if((delay.ms = ms) > 0) {
        if(!(delay.callback = callback)) {
            while(delay.ms)
                task_execute(true);
        }
    } else {
        delay.callback = NULL;
        if(callback)
            callback();
    }
}

static uint64_t getElapsedMicros (void)
{
    uint32_t ms, cycles;

    do {
        __LDREXW(&systick_safe_read);
        ms = tick_ms;
        cycles = tick_cycles;
    } while(__STREXW(1, &systick_safe_read));

    uint32_t ccdelta = DWT->CYCCNT - cycles;
    uint32_t frac = (uint32_t)(((uint64_t)ccdelta * cycles2us_factor) >> 32);

    return (uint64_t)ms * 1000U + (frac > 1000U ? 1000U : frac);
}

static uint32_t getElapsedTicks (void)
{
    return tick_ms;
}

/* ---------------------------------------------------------------------- */
/*  Steppers                                                               */
/* ---------------------------------------------------------------------- */

// Enable/disable stepper motors
static void stepperEnable (axes_signals_t enable, bool hold)
{
    (void)hold;

    enable.mask ^= settings.steppers.enable_invert.mask;
    DIGITAL_OUT(STEPPERS_ENABLE_PORT, STEPPERS_ENABLE_PIN, enable.x);
}

// Starts the stepper driver interrupt, the first callback comes after ~2 ms
static void stepperWakeUp (void)
{
    hal.stepper.enable((axes_signals_t){AXES_BITMASK}, false);

    STEPPER_TIMER->ARR = hal.f_step_timer / 500; // ~2ms delay to allow drivers time to wake up.
    STEPPER_TIMER->CNT = 0;
    STEPPER_TIMER->EGR = TIM_EGR_UG;            // Load the prescaler
    STEPPER_TIMER->SR = 0;
    STEPPER_TIMER->DIER = TIM_DIER_UIE;
}

// Sets stepper driver interrupt timeout
// Called from the step interrupt, right after the update event: the counter
// is small. ARR isn't preloaded, should the counter already be past the new
// period an update is forced (the next step comes a little early instead of
// after a counter wrap).
ISR_CODE static void stepperCyclesPerTick (uint32_t cycles_per_tick)
{
    uint32_t period = cycles_per_tick < (1UL << 20) ? max(cycles_per_tick, step_pulse.t_min_period) : 0x000FFFFFUL;

    STEPPER_TIMER->ARR = period - 1;
    if(STEPPER_TIMER->CNT >= period - 1) {
        STEPPER_TIMER->CNT = 0;
        STEPPER_TIMER->EGR = TIM_EGR_UG;
    }
}

// Set stepper pulse output pins, bit0 -> X, bit1 -> Y, bit2 -> Z.
// Inversion ($2) is part of the lookup table.
__STATIC_FORCEINLINE void stepper_step_out (axes_signals_t step_out)
{
    STEP_PORT->BSRR = step_bsrr[step_out.bits & 7U];
}

// Set stepper direction output pins, inversion ($3) is in the table.
__STATIC_FORCEINLINE void stepper_dir_out (axes_signals_t dir_out)
{
    DIRECTION_PORT->BSRR = dir_bsrr[dir_out.bits & 7U];
}

// Build the BSRR lookup tables for the step and direction outputs.
static void stepdir_map_init (settings_t *settings)
{
    static const uint8_t step_pin[3] = { X_STEP_PIN, Y_STEP_PIN, Z_STEP_PIN };
    static const uint8_t dir_pin[3] = { X_DIRECTION_PIN, Y_DIRECTION_PIN, Z_DIRECTION_PIN };
    uint_fast8_t bits, axis;

    for(bits = 0; bits < 8; bits++) {

        uint32_t step = 0, dir = 0;
        uint_fast8_t step_lvl = bits ^ settings->steppers.step_invert.bits,
                     dir_lvl = bits ^ settings->steppers.dir_invert.bits;

        for(axis = 0; axis < 3; axis++) {
            step |= (step_lvl & (1U << axis)) ? (1UL << step_pin[axis]) : (1UL << (step_pin[axis] + 16U));
            dir |= (dir_lvl & (1U << axis)) ? (1UL << dir_pin[axis]) : (1UL << (dir_pin[axis] + 16U));
        }
        step_bsrr[bits] = step;
        dir_bsrr[bits] = dir;
    }
}

// Disables stepper driver interrupts
static void stepperGoIdle (bool clear_signals)
{
    STEPPER_TIMER->DIER &= ~TIM_DIER_UIE;

    if(clear_signals) {
        stepper_dir_out((axes_signals_t){0});
        stepper_step_out((axes_signals_t){0});
    }
}

/*
  The step timer counts up from 0 to ARR, the update (timeout) interrupt
  starts the next step: the core calls hal.stepper.pulse_start(). The pulse
  is ended by the compare 1 interrupt, t_off ticks after it started.
  ARR is at least t_min_period (pulse + minimum off time), so the pulse
  always ends before the next update. Should the compare interrupt be late
  anyway, the update handler ends the pulse first.

  With a step pulse delay ($29) the direction outputs change first and the
  step pulse starts t_on ticks later, in the compare 2 interrupt.
*/
__STATIC_FORCEINLINE void _stepper_step_out (axes_signals_t step_out)
{
    stepper_step_out(step_out);

    STEPPER_TIMER->CCR1 = STEPPER_TIMER->CNT + step_pulse.t_off;
    STEPPER_TIMER->SR = ~TIM_SR_CC1IF;
    STEPPER_TIMER->DIER |= TIM_DIER_CC1IE;
}

// Sets stepper direction and pulse pins and starts a step pulse.
ISR_CODE static void stepperPulseStart (stepper_t *stepper)
{
    if(stepper->dir_changed.bits) {
        stepper->dir_changed.bits = 0;
        stepper_dir_out(stepper->dir_out);
    }

    if(stepper->step_out.bits)
        _stepper_step_out(stepper->step_out);
}

// Start a stepper pulse, delay version ($29 > 0).
// Note: delay is only added when there is a direction change and a pulse to be output.
ISR_CODE static void stepperPulseStartDelayed (stepper_t *stepper)
{
    if(stepper->dir_changed.bits) {

        stepper_dir_out(stepper->dir_out);

        if(stepper->step_out.bits) {

            if(stepper->step_out.bits & stepper->dir_changed.bits) {

                step_pulse.out = stepper->step_out; // Store out_bits

                STEPPER_TIMER->CCR2 = STEPPER_TIMER->CNT + step_pulse.t_on;
                STEPPER_TIMER->SR = ~TIM_SR_CC2IF;
                STEPPER_TIMER->DIER |= TIM_DIER_CC2IE;

            } else
                _stepper_step_out(stepper->step_out);
        }

        stepper->dir_changed.bits = 0;

        return;
    }

    if(stepper->step_out.bits)
        _stepper_step_out(stepper->step_out);
}

/* ---------------------------------------------------------------------- */
/*  Limits, control, probe                                                 */
/* ---------------------------------------------------------------------- */

// Enable/disable limit pins interrupt
static void limitsEnable (bool on, axes_signals_t homing_cycle)
{
    bool disable = !on;
    axes_signals_t pin;
    input_signal_t *limit;
    uint_fast8_t idx = limit_inputs.n_pins;
    limit_signals_t homing_source = xbar_get_homing_source_from_cycle(homing_cycle);

    while(idx--) {
        limit = &limit_inputs.pins.inputs[idx];
        if(on && homing_cycle.mask) {
            pin = xbar_fn_to_axismask(limit->id);
            disable = limit->group == PinGroup_Limit ? (pin.mask & homing_source.min.mask) : (pin.mask & homing_source.max.mask);
        }
        gpio_irq_enable(limit, disable ? IRQ_Mode_None : (pin_irq_mode_t)limit->mode.irq_mode);
    }
}

// Returns limit state as an limit_signals_t variable.
// Each bitfield bit indicates an axis limit, where triggered is 1 and not triggered is 0.
static limit_signals_t limitsGetState (void)
{
    limit_signals_t signals = {0};

    signals.min.mask = settings.limits.invert.mask;
    signals.min.value = (uint8_t)((LIMIT_PORT->IDR & LIMIT_MASK) >> LIMIT_INMODE);

    if(settings.limits.invert.mask)
        signals.min.value ^= settings.limits.invert.mask;

    return signals;
}

// Returns system state as a control_signals_t variable.
// Each bitfield bit indicates a control signal, where triggered is 1 and not triggered is 0.
static control_signals_t systemGetState (void)
{
    control_signals_t signals = { settings.control_invert.mask };

#if defined(RESET_PIN) && !ESTOP_ENABLE
    signals.reset = DIGITAL_IN(RESET_PORT, RESET_PIN);
#endif
#if defined(RESET_PIN) && ESTOP_ENABLE
    signals.e_stop = DIGITAL_IN(RESET_PORT, RESET_PIN);
#endif
#ifdef FEED_HOLD_PIN
    signals.feed_hold = DIGITAL_IN(FEED_HOLD_PORT, FEED_HOLD_PIN);
#endif
#ifdef CYCLE_START_PIN
    signals.cycle_start = DIGITAL_IN(CYCLE_START_PORT, CYCLE_START_PIN);
#endif
#ifdef SAFETY_DOOR_PIN
    if(debounce.safety_door)
        signals.safety_door_ajar = !settings.control_invert.safety_door_ajar;
    else
        signals.safety_door_ajar = DIGITAL_IN(SAFETY_DOOR_PORT, SAFETY_DOOR_PIN);
#endif

    if(settings.control_invert.mask)
        signals.value ^= settings.control_invert.mask;

    return aux_ctrl_scan_status(signals);
}

#if DRIVER_PROBES

// Returns the probe triggered pin state.
static bool probeGetState (void *input)
{
    return DIGITAL_IN(((input_signal_t *)input)->port, ((input_signal_t *)input)->pin);
}

#endif

static void aux_irq_handler (uint8_t port, bool state)
{
    aux_ctrl_t *aux_in;
    control_signals_t signals = {0};

    (void)state;

    if((aux_in = aux_ctrl_in_get(port))) {
        signals.mask |= aux_in->signal.mask;
        if(aux_in->irq_mode == IRQ_Mode_Change)
            signals.deasserted = hal.port.wait_on_input(Port_Digital, aux_in->port, WaitMode_Immediate, 0.0f) == 0;
    }

    if(signals.mask) {
        if(!signals.deasserted)
            signals.mask |= systemGetState().mask;
        hal.control.interrupt_callback(signals);
    }
}

static bool aux_claim_explicit (aux_ctrl_t *aux_ctrl)
{
    xbar_t *pin;

    if(aux_ctrl->input == NULL) {

        uint_fast8_t i = N_INPUTS;

        do {
            --i;
            if(inputpin[i].group == PinGroup_AuxInput && inputpin[i].user_port == aux_ctrl->port)
                aux_ctrl->input = &inputpin[i];
        } while(i && aux_ctrl->input == NULL);
    }

    if((pin = aux_ctrl_claim_port(aux_ctrl))) {

        switch(aux_ctrl->function) {
#if PROBE_ENABLE
            case Input_Probe:
                hal.driver_cap.probe = probe_add(Probe_Default, aux_ctrl->port, (pin_irq_mode_t)pin->cap.irq_mode, aux_ctrl->input, probeGetState);
                break;
#endif
#if SAFETY_DOOR_ENABLE || (defined(RESET_PIN) && !ESTOP_ENABLE)
  #if defined(RESET_PIN) && !ESTOP_ENABLE
            case Input_Reset:
  #endif
  #if SAFETY_DOOR_ENABLE
            case Input_SafetyDoor:
  #endif
                ((input_signal_t *)aux_ctrl->input)->mode.debounce = ((input_signal_t *)aux_ctrl->input)->cap.debounce && hal.driver_cap.software_debounce;
                break;
#endif
            default:
                break;
        }
    }

    return aux_ctrl->port != IOPORT_UNASSIGNED;
}

// Assign aux input ports and find out which ones can raise interrupts:
// one EXTI line per pin number, limit switches have priority.
static void aux_assign_irq (void)
{
    uint32_t i, j, irq = 0;
    input_signal_t *input, *input2;
    aux_ctrl_t *aux;
    pin_group_pins_t aux_digital_in = {0};

    const control_signals_t main_signals = { .reset = On, .e_stop = On, .feed_hold = On, .cycle_start = On };

    for(i = 0; i < N_INPUTS; i++) {

        input = &inputpin[i];

        if(input->group == PinGroup_AuxInput) {

            input->bit = 1UL << input->pin;

            if(aux_digital_in.pins.inputs == NULL)
                aux_digital_in.pins.inputs = input;

            input->user_port = aux_digital_in.n_pins++;
            input->id = (pin_function_t)(Input_Aux0 + input->user_port);
            input->mode.pull_mode = PullMode_Up;
            input->cap.pull_mode = PullMode_UpDown;
            input->cap.irq_mode = (DRIVER_IRQMASK & input->bit) ? IRQ_Mode_None : IRQ_Mode_Edges;

            aux = aux_ctrl_get_fn((aux_gpio_t){ .port = input->port, .pin = input->pin });

            if(input->cap.irq_mode == IRQ_Mode_None) {
                if(aux && xbar_is_probe_in(aux->function))
                    input->id = aux->function;
            } else {

                if(aux)
                    input->id = aux->function;

                if(irq & input->bit) { // duplicate IRQ

                    if(aux == NULL)
                        input->cap.irq_mode = IRQ_Mode_None;
                    else for(j = 0; j < aux_digital_in.n_pins - 1U; j++) {
                        input2 = &aux_digital_in.pins.inputs[j];
                        if(input->pin == input2->pin) {
                            if(input->id < input2->id || (aux->signal.bits & main_signals.bits)) {
                                input2->cap.irq_mode = IRQ_Mode_None;
                                if(!xbar_is_probe_in(input2->id))
                                    input2->id = (pin_function_t)(Input_Aux0 + input2->user_port);
                            } else {
                                input->cap.irq_mode = IRQ_Mode_None;
                                if(!xbar_is_probe_in(input->id))
                                    input->id = (pin_function_t)(Input_Aux0 + input->user_port);
                            }
                        }
                    }
                } else
                    irq |= input->bit;
            }
        }
    }
}

/* ---------------------------------------------------------------------- */
/*  Coolant                                                                */
/* ---------------------------------------------------------------------- */

// Start/stop coolant (and mist if enabled)
static void coolantSetState (coolant_state_t mode)
{
    mode.value ^= settings.coolant.invert.mask;
#ifdef COOLANT_FLOOD_PIN
    DIGITAL_OUT(COOLANT_FLOOD_PORT, COOLANT_FLOOD_PIN, mode.flood);
#endif
#ifdef COOLANT_MIST_PIN
    DIGITAL_OUT(COOLANT_MIST_PORT, COOLANT_MIST_PIN, mode.mist);
#endif
}

// Returns coolant state in a coolant_state_t variable
static coolant_state_t coolantGetState (void)
{
    coolant_state_t state = { .mask = settings.coolant.invert.mask };

#ifdef COOLANT_FLOOD_PIN
    state.flood = (COOLANT_FLOOD_PORT->ODR >> COOLANT_FLOOD_PIN) & 1U;
#endif
#ifdef COOLANT_MIST_PIN
    state.mist = (COOLANT_MIST_PORT->ODR >> COOLANT_MIST_PIN) & 1U;
#endif
    state.value ^= settings.coolant.invert.mask;

    return state;
}

/* ---------------------------------------------------------------------- */
/*  Atomic helpers                                                         */
/* ---------------------------------------------------------------------- */

// Helper functions for setting/clearing/inverting individual bits atomically (uninterruptable)
static void bitsSetAtomic (volatile uint_fast16_t *ptr, uint_fast16_t bits)
{
    __disable_irq();
    *ptr |= bits;
    __enable_irq();
}

static uint_fast16_t bitsClearAtomic (volatile uint_fast16_t *ptr, uint_fast16_t bits)
{
    __disable_irq();
    uint_fast16_t prev = *ptr;
    *ptr &= ~bits;
    __enable_irq();

    return prev;
}

static uint_fast16_t valueSetAtomic (volatile uint_fast16_t *ptr, uint_fast16_t value)
{
    __disable_irq();
    uint_fast16_t prev = *ptr;
    *ptr = value;
    __enable_irq();

    return prev;
}

static void irqEnable (void)
{
    __enable_irq();
}

static void irqDisable (void)
{
    __disable_irq();
}

static void driverReboot (void)
{
    NVIC_SystemReset();
}

/* ---------------------------------------------------------------------- */
/*  Settings                                                               */
/* ---------------------------------------------------------------------- */

#define EXTI_LINES_0_4   0x001FUL
#define EXTI_LINES_5_9   0x03E0UL
#define EXTI_LINES_10_15 0xFC00UL

static void exti_nvic (uint32_t lines, bool enable)
{
    static const struct { uint32_t mask; IRQn_Type irq; } exti[] = {
        { 1UL << 0, EXTI0_IRQn }, { 1UL << 1, EXTI1_IRQn }, { 1UL << 2, EXTI2_IRQn },
        { 1UL << 3, EXTI3_IRQn }, { 1UL << 4, EXTI4_IRQn },
        { EXTI_LINES_5_9, EXTI9_5_IRQn }, { EXTI_LINES_10_15, EXTI15_10_IRQn }
    };
    uint_fast8_t i;

    for(i = 0; i < sizeof(exti) / sizeof(exti[0]); i++) {
        if(lines & exti[i].mask) {
            if(enable) {
                NVIC_SetPriority(exti[i].irq, IRQ_PRIO_INPUTS);
                NVIC_ClearPendingIRQ(exti[i].irq);
                NVIC_EnableIRQ(exti[i].irq);
            } else
                NVIC_DisableIRQ(exti[i].irq);
        }
    }
}

// Configures peripherals when settings are initialized or changed
static void on_settings_changed (settings_t *settings, settings_changed_flags_t changed)
{
    (void)changed;

    if(IOInitDone) {

        stepdir_map_init(settings);

        hal.stepper.go_idle(true);

        float sl = (float)hal.f_step_timer / 1000000.0f;

        if(hal.driver_cap.step_pulse_delay && settings->steppers.pulse_delay_microseconds > 0.0f) {
            step_pulse.t_on = (uint32_t)ceilf(sl * (max(STEP_PULSE_TOFF_MIN, settings->steppers.pulse_delay_microseconds) - STEP_PULSE_TOFF_LATENCY));
            hal.stepper.pulse_start = stepperPulseStartDelayed;
        } else {
            step_pulse.t_on = 0;
            hal.stepper.pulse_start = stepperPulseStart;
        }

        step_pulse.t_min_period = (uint32_t)ceilf(sl * (settings->steppers.pulse_microseconds + STEP_PULSE_TOFF_MIN)) + step_pulse.t_on;
        step_pulse.t_off = (uint32_t)ceilf(sl * (settings->steppers.pulse_microseconds - STEP_PULSE_TOFF_LATENCY));

        /*************************
         *  Control pins config  *
         *************************/

        exti_nvic(DRIVER_IRQMASK|aux_irq, false);

        uint32_t i = N_INPUTS;
        input_signal_t *input;

        axes_signals_t limit_fei;
        limit_fei.mask = settings->limits.disable_pullup.mask ^ settings->limits.invert.mask;

        do {

            input = &inputpin[--i];

            if(input->group != PinGroup_AuxInput)
                input->mode.irq_mode = IRQ_Mode_None;

            switch(input->id) {

                case Input_LimitX:
                    input->mode.pull_mode = settings->limits.disable_pullup.x ? PullMode_None : PullMode_Up;
                    input->mode.irq_mode = limit_fei.x ? IRQ_Mode_Falling : IRQ_Mode_Rising;
                    break;

                case Input_LimitY:
                    input->mode.pull_mode = settings->limits.disable_pullup.y ? PullMode_None : PullMode_Up;
                    input->mode.irq_mode = limit_fei.y ? IRQ_Mode_Falling : IRQ_Mode_Rising;
                    break;

                case Input_LimitZ:
                    input->mode.pull_mode = settings->limits.disable_pullup.z ? PullMode_None : PullMode_Up;
                    input->mode.irq_mode = limit_fei.z ? IRQ_Mode_Falling : IRQ_Mode_Rising;
                    break;

                default:
                    break;
            }

            // EXTI line mapping for every pin which may raise an interrupt.
            if(input->group == PinGroup_Limit || (input->group == PinGroup_AuxInput && input->cap.irq_mode != IRQ_Mode_None))
                exti_map(input);

            gpio_config(input->port, input->pin, GpioMode_Input, false, (pull_mode_t)input->mode.pull_mode, 0);

            if(input->group == PinGroup_Limit) {
                EXTI->IMR &= ~input->bit;
                gpio_irq_enable(input, (pin_irq_mode_t)input->mode.irq_mode);
            }

        } while(i);

        uint32_t irq_mask = DRIVER_IRQMASK|aux_irq;

        EXTI->PR = irq_mask;
        exti_nvic(irq_mask, true);

        hal.limits.enable(settings->limits.flags.hard_enabled, (axes_signals_t){0});
        aux_ctrl_irq_enable(settings, aux_irq_handler);
    }
}

/* ---------------------------------------------------------------------- */
/*  Pin enumeration ($pins)                                                */
/* ---------------------------------------------------------------------- */

static char *port2char (GPIO_TypeDef *port)
{
    static char name[3] = "P?";

    name[1] = (char)('A' + GPIO_INDEX(port));

    return name;
}

static void enumeratePins (bool low_level, pin_info_ptr pin_info, void *data)
{
    static xbar_t pin = {0};

    uint8_t i, id = 0;

    pin.mode.input = On;

    for(i = 0; i < N_INPUTS; i++) {
        pin.id = id++;
        pin.pin = inputpin[i].pin;
        pin.function = inputpin[i].id;
        pin.group = inputpin[i].group;
        pin.port = low_level ? (void *)inputpin[i].port : (void *)port2char(inputpin[i].port);
        pin.description = inputpin[i].description;

        pin_info(&pin, data);
    }

    pin.mode.mask = 0;
    pin.mode.output = On;

    for(i = 0; i < N_OUTPUTS; i++) {
        pin.id = id++;
        pin.pin = outputpin[i].pin;
        pin.function = outputpin[i].id;
        pin.group = outputpin[i].group;
        pin.port = low_level ? (void *)outputpin[i].port : (void *)port2char(outputpin[i].port);
        pin.description = outputpin[i].description;
        pin.mode.pwm = outputpin[i].mode.pwm;

        pin_info(&pin, data);
    }

    periph_signal_t *ppin = periph_pins;

    if(ppin) do {
        pin.id = id++;
        pin.pin = ppin->pin.pin;
        pin.function = ppin->pin.function;
        pin.group = ppin->pin.group;
        pin.port = low_level ? ppin->pin.port : (void *)port2char(ppin->pin.port);
        pin.mode = ppin->pin.mode;
        pin.description = ppin->pin.description == NULL ? xbar_group_to_description(ppin->pin.group) : ppin->pin.description;

        pin_info(&pin, data);
    } while((ppin = ppin->next));
}

static void registerPeriphPin (const periph_pin_t *pin)
{
    periph_signal_t *add_pin = malloc(sizeof(periph_signal_t));

    if(!add_pin)
        return;

    memcpy(&add_pin->pin, pin, sizeof(periph_pin_t));
    add_pin->next = NULL;

    if(periph_pins == NULL) {
        periph_pins = add_pin;
    } else {
        periph_signal_t *last = periph_pins;
        while(last->next)
            last = last->next;
        last->next = add_pin;
    }
}

static void setPeriphPinDescription (const pin_function_t function, const pin_group_t group, const char *description)
{
    periph_signal_t *ppin = periph_pins;

    if(ppin) do {
        if(ppin->pin.function == function && ppin->pin.group == group) {
            ppin->pin.description = description;
            ppin = NULL;
        } else
            ppin = ppin->next;
    } while(ppin);
}

/* ---------------------------------------------------------------------- */
/*  Setup                                                                  */
/* ---------------------------------------------------------------------- */

// Called once by the core after the settings are loaded.
static bool driver_setup (settings_t *settings)
{
    uint32_t i;
    axes_signals_t st_enable = st_get_enable_out();

    /*************************
     *  Output signals init  *
     *************************/

    stepdir_map_init(settings);

    for(i = 0; i < N_OUTPUTS; i++) {
        if(!(outputpin[i].group == PinGroup_AuxOutputAnalog || outputpin[i].id == Output_SpindlePWM)) {

            // Initial level before the pin becomes an output: drivers off.
            if(outputpin[i].group == PinGroup_StepperEnable)
                DIGITAL_OUT(outputpin[i].port, outputpin[i].pin, st_enable.x)
            else if(outputpin[i].group == PinGroup_StepperStep)
                DIGITAL_OUT(outputpin[i].port, outputpin[i].pin, (settings->steppers.step_invert.mask & xbar_fn_to_axismask(outputpin[i].id).mask) != 0)
            else if(outputpin[i].group == PinGroup_LED)
                DIGITAL_OUT(outputpin[i].port, outputpin[i].pin, 0) // Black Pill LED is active low: on.

            gpio_config(outputpin[i].port, outputpin[i].pin, GpioMode_Output, outputpin[i].mode.open_drain, PullMode_None, 0);
        }
    }

    /*************************
     *  Stepper timer         *
     *************************/

    RCC->APB1ENR |= RCC_APB1ENR_TIM5EN;
    (void)RCC->APB1ENR;
    STEPPER_TIMER->CR1 = 0;                         // Stopped, e.g. after a bootloader
    STEPPER_TIMER->DIER = 0;
    STEPPER_TIMER->PSC = STEPPER_TIMER_DIV - 1;
    STEPPER_TIMER->ARR = 0xFFFFFFFFUL;
    STEPPER_TIMER->EGR = TIM_EGR_UG;                // Load the prescaler
    STEPPER_TIMER->SR = 0;
    // Up counting, ARR not preloaded. The timer runs all the time, the
    // step interrupt is switched with the update interrupt enable only.
    STEPPER_TIMER->CR1 = TIM_CR1_CEN;

    NVIC_SetPriority(STEPPER_TIMER_IRQn, IRQ_PRIO_STEPPER);
    NVIC_EnableIRQ(STEPPER_TIMER_IRQn);

    IOInitDone = settings->version.id == SETTINGS_VERSION;

    grbl.on_settings_changed(settings, (settings_changed_flags_t){0});

    return IOInitDone;
}

static uint32_t get_free_mem (void)
{
    // Free heap is not tracked by the C libraries in a portable way (IAR
    // DLIB, newlib nano): report 0, which the core shows as "unknown".
    return 0;
}

#if USB_SERIAL_CDC

static status_code_t enter_dfu (sys_state_t state, char *args)
{
    (void)state;
    (void)args;

    report_message("Entering DFU Bootloader", Message_Warning);
    hal.delay_ms(100, NULL);

    cpu_reboot_to_bootloader();

    return Status_OK;
}

#endif

static void onReportOptions (bool newopt);
static on_report_options_ptr on_report_options;

static void onReportOptions (bool newopt)
{
    on_report_options(newopt);

    if(!newopt) {
        report_plugin("Register level BlackPill driver", "0.01");
        if(cpu_clock_source != CpuClock_HSE)
            report_message(cpu_clock_source == CpuClock_HSI ? "No crystal, running on HSI" : "PLL failed, clocks wrong!", Message_Warning);
    }
}

// Main entry point of the driver, called once by the core.
bool driver_init (void)
{
#if defined(STM32F401xE)
    hal.info = "STM32F401CE";
#else
    hal.info = "STM32F401CC";
#endif
    hal.driver_version = "261007";
    hal.driver_url = "https://github.com/alexsys87/grbl_BlackPill";
    hal.board = BOARD_NAME;
#ifdef BOARD_URL
    hal.board_url = BOARD_URL;
#endif
    hal.driver_setup = driver_setup;
    hal.f_mcu = SystemCoreClock / 1000000UL;
    hal.f_step_timer = (SystemCoreClock / 2UL) * 2UL / STEPPER_TIMER_DIV; // APB1 timer clock / 4
    hal.rx_buffer_size = RX_BUFFER_SIZE;
    hal.get_free_mem = get_free_mem;
    hal.delay_ms = driver_delay;
    grbl.on_settings_changed = on_settings_changed;

    cycles2us_factor = 0xFFFFFFFFU / hal.f_mcu;

    hal.stepper.wake_up = stepperWakeUp;
    hal.stepper.go_idle = stepperGoIdle;
    hal.stepper.enable = stepperEnable;
    hal.stepper.cycles_per_tick = stepperCyclesPerTick;
    hal.stepper.pulse_start = stepperPulseStart;
    hal.stepper.motor_iterator = motor_iterator;

    hal.limits.enable = limitsEnable;
    hal.limits.get_state = limitsGetState;

    hal.coolant.set_state = coolantSetState;
    hal.coolant.get_state = coolantGetState;

    hal.control.get_state = systemGetState;

    hal.reboot = driverReboot;
    hal.irq_enable = irqEnable;
    hal.irq_disable = irqDisable;
    hal.set_bits_atomic = bitsSetAtomic;
    hal.clear_bits_atomic = bitsClearAtomic;
    hal.set_value_atomic = valueSetAtomic;
    hal.get_micros = getElapsedMicros;
    hal.get_elapsed_ticks = getElapsedTicks;
    hal.enumerate_pins = enumeratePins;
    hal.periph_port.register_pin = registerPeriphPin;
    hal.periph_port.set_pin_description = setPeriphPinDescription;

    on_report_options = grbl.on_report_options;
    grbl.on_report_options = onReportOptions;

    serialRegisterStreams();

#if USB_SERIAL_CDC

    static const sys_command_t boot_command_list[] = {
        {"DFU", enter_dfu, { .allow_blocking = On, .noargs = On }, { .str = "enter DFU bootloader" } }
    };

    static sys_commands_t boot_commands = {
        .n_commands = sizeof(boot_command_list) / sizeof(sys_command_t),
        .commands = boot_command_list
    };

    stream_connect(usbInit());
    system_register_commands(&boot_commands);

#else
    if(!stream_connect_instance(SERIAL_STREAM, BAUD_RATE))
        while(true); // Cannot boot if no communication channel is available!
#endif

#if FLASH_ENABLE
    hal.nvs.type = NVS_Flash;
    hal.nvs.size_max = 1024 * 16;
    hal.nvs.memcpy_from_flash = memcpy_from_flash;
    hal.nvs.memcpy_to_flash = memcpy_to_flash;
#else
    hal.nvs.type = NVS_None;
#endif

// driver capabilities, used for announcing and negotiating (with the core) driver functionality

    hal.limits_cap = get_limits_cap();
    hal.home_cap = get_home_cap();
    hal.coolant_cap.bits = COOLANT_ENABLE;
    hal.driver_cap.software_debounce = On;
    hal.driver_cap.step_pulse_delay = On;
    hal.driver_cap.amass_level = 3;
    hal.driver_cap.control_pull_up = On;
    hal.driver_cap.limits_pull_up = On;

    static pin_group_pins_t aux_digital_in = {0}, aux_digital_out = {0};

    uint32_t i;
    input_signal_t *input;

    aux_assign_irq();

    for(i = 0; i < N_INPUTS; i++) {

        input = &inputpin[i];
        input->mode.input = input->cap.input = On;
        input->bit = 1UL << input->pin;

        switch(input->group) {

            case PinGroup_AuxInput:
                if(aux_digital_in.pins.inputs == NULL)
                    aux_digital_in.pins.inputs = input;

                aux_digital_in.n_pins++;

                if(!(input->id >= Input_Aux0 && input->id <= Input_AuxMax)) {
                    input->id = (pin_function_t)(Input_Aux0 + input->user_port);
                    aux_ctrl_remap_explicit((aux_gpio_t){ .port = input->port, .pin = input->pin }, input->user_port, input);
                }

                if((input->cap.debounce = input->cap.irq_mode != IRQ_Mode_None)) {
                    aux_irq |= input->bit;
                    pin_irq[input->pin] = input;
                }
                break;

            case PinGroup_Limit:
            case PinGroup_LimitMax:
                if(limit_inputs.pins.inputs == NULL)
                    limit_inputs.pins.inputs = input;
                if(LIMIT_MASK & input->bit)
                    pin_irq[input->pin] = input;
                limit_inputs.n_pins++;
                break;

            default: break;
        }
    }

    output_signal_t *output;

    for(i = 0; i < N_OUTPUTS; i++) {

        output = &outputpin[i];
        output->mode.output = On;

        if(output->group == PinGroup_AuxOutput) {
            if(aux_digital_out.pins.outputs == NULL)
                aux_digital_out.pins.outputs = output;
            output->id = (pin_function_t)(Output_Aux0 + aux_digital_out.n_pins);
            aux_out_remap_explicit((aux_gpio_t){ .port = output->port, .pin = output->pin }, aux_digital_out.n_pins, output);
            aux_digital_out.n_pins++;
        }
    }

    if(aux_digital_in.n_pins || aux_digital_out.n_pins)
        ioports_init(&aux_digital_in, &aux_digital_out);

    aux_ctrl_claim_ports(aux_claim_explicit, NULL);
    aux_ctrl_claim_out_ports(aux_out_claim_explicit, NULL);

#if DRIVER_SPINDLE_ENABLE
    driver_spindles_init();
#endif

#include "grbl/plugins_init.h"

    // No need to move version check before init.
    // Compiler will fail any signature mismatch for existing entries.
    return hal.version == 10;
}

/* ---------------------------------------------------------------------- */
/*  Interrupt handlers                                                     */
/* ---------------------------------------------------------------------- */

// Main stepper driver
ISR_CODE void STEPPER_TIMER_IRQHandler (void);
ISR_CODE void STEPPER_TIMER_IRQHandler (void)
{
    uint32_t pending = STEPPER_TIMER->SR & STEPPER_TIMER->DIER;

    // Step pulse off
    if(pending & TIM_SR_CC1IF) {
        STEPPER_TIMER->DIER &= ~TIM_DIER_CC1IE;
        STEPPER_TIMER->SR = ~TIM_SR_CC1IF;
        stepper_step_out((axes_signals_t){0});
    }

    // Delayed step pulse start
    if(pending & TIM_SR_CC2IF) {
        STEPPER_TIMER->DIER &= ~TIM_DIER_CC2IE;
        STEPPER_TIMER->SR = ~TIM_SR_CC2IF;
        _stepper_step_out(step_pulse.out);
    }

    // Stepper timeout: next step
    if(pending & TIM_SR_UIF) {
        STEPPER_TIMER->SR = ~TIM_SR_UIF;
        if(STEPPER_TIMER->DIER & (TIM_DIER_CC1IE|TIM_DIER_CC2IE)) {
            // Late compare interrupt: end the pulse / output the delayed one now.
            if(STEPPER_TIMER->DIER & TIM_DIER_CC2IE)
                stepper_step_out(step_pulse.out);
            STEPPER_TIMER->DIER &= ~(TIM_DIER_CC1IE|TIM_DIER_CC2IE);
            stepper_step_out((axes_signals_t){0});
        }
        hal.stepper.interrupt_callback();
    }
}

static void core_pin_debounce (void *pin)
{
    input_signal_t *input = (input_signal_t *)pin;

    if(input->mode.irq_mode == IRQ_Mode_Change ||
         DIGITAL_IN(input->port, input->pin) == (input->mode.irq_mode == IRQ_Mode_Falling ? 0U : 1U)) {

        if(input->group & (PinGroup_Limit|PinGroup_LimitMax)) {
            limit_signals_t state = limitsGetState();
            if(limit_signals_merge(state).value)
                hal.limits.interrupt_callback(state);
        }
    }

    EXTI->IMR |= input->bit; // Reenable pin interrupt
}

static inline void core_pin_irq (uint32_t bits)
{
    input_signal_t *input;
    uint_fast8_t line;

    for(line = 0; line < 16; line++) {
        if((bits & (1UL << line)) && (input = pin_irq[line])) {
            if(input->mode.debounce && task_add_delayed(core_pin_debounce, input, 40))
                EXTI->IMR &= ~input->bit; // Disable pin interrupt
            else
                core_pin_debounce(input);
        }
    }
}

static void aux_pin_debounce (void *pin)
{
    input_signal_t *input = (input_signal_t *)pin;

#if SAFETY_DOOR_ENABLE
    if(input->id == Input_SafetyDoor)
        debounce.safety_door = Off;
#endif

    if(input->mode.irq_mode == IRQ_Mode_Change ||
          DIGITAL_IN(input->port, input->pin) == (input->mode.irq_mode == IRQ_Mode_Falling ? 0U : 1U))
        ioports_event(input);

    EXTI->IMR |= input->bit; // Reenable pin interrupt
}

static inline void aux_pin_irq (uint32_t bits)
{
    input_signal_t *input;
    uint_fast8_t line;

    for(line = 0; line < 16; line++) {
        if((bits & (1UL << line)) && (input = pin_irq[line]) && input->group == PinGroup_AuxInput) {
            if(input->mode.debounce && task_add_delayed(aux_pin_debounce, input, 40)) {
                EXTI->IMR &= ~input->bit; // Disable pin interrupt
#if SAFETY_DOOR_ENABLE
                if(input->id == Input_SafetyDoor)
                    debounce.safety_door = input->mode.debounce;
#endif
            } else
                ioports_event(input);
        }
    }
}

// Common EXTI dispatcher: limit lines and aux input lines.
static void exti_irq (uint32_t lines)
{
    uint32_t ifg = EXTI->PR & lines;

    if(ifg) {
        EXTI->PR = ifg;
        if(ifg & LIMIT_MASK)
            core_pin_irq(ifg & LIMIT_MASK);
        if(ifg & aux_irq)
            aux_pin_irq(ifg & aux_irq);
    }
}

void EXTI0_IRQHandler (void);
void EXTI1_IRQHandler (void);
void EXTI2_IRQHandler (void);
void EXTI3_IRQHandler (void);
void EXTI4_IRQHandler (void);
void EXTI9_5_IRQHandler (void);
void EXTI15_10_IRQHandler (void);

void EXTI0_IRQHandler (void)     { exti_irq(1UL << 0); }
void EXTI1_IRQHandler (void)     { exti_irq(1UL << 1); }
void EXTI2_IRQHandler (void)     { exti_irq(1UL << 2); }
void EXTI3_IRQHandler (void)     { exti_irq(1UL << 3); }
void EXTI4_IRQHandler (void)     { exti_irq(1UL << 4); }
void EXTI9_5_IRQHandler (void)   { exti_irq(EXTI_LINES_5_9); }
void EXTI15_10_IRQHandler (void) { exti_irq(EXTI_LINES_10_15); }

// 1 ms tick
void SysTick_Handler (void);
void SysTick_Handler (void)
{
    tick_cycles = DWT->CYCCNT;
    tick_ms++;

    if(delay.ms && !(--delay.ms)) {
        if(delay.callback) {
            delay.callback();
            delay.callback = NULL;
        }
    }
}
