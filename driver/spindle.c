/*
  spindle.c - PWM spindle (and laser) for the STM32F401 Black Pill driver,
              register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).
  Follows the grblHAL STM32F4xx driver (Copyright (c) 2024-2026 Terje Io).

    PA8  TIM1_CH1  PWM, $33 frequency, $34..$36 PWM values
    PB1            spindle enable (M3 / M4 on, M5 off)
    PB2            spindle direction (M4)

  With laser mode ($32=1) the PWM follows the feed in real time, M3 S...
  is constant power, M4 S... is dynamic power.

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

#include "driver.h"

#include "grbl/task.h"
#include "grbl/report.h"

#if DRIVER_SPINDLE_ENABLE

static spindle_id_t spindle_id = -1;
static settings_changed_ptr on_settings_changed;

#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM

#if SPINDLE_PWM_PIN != 8 || !defined(SPINDLE_PWM_PORT)
#error "The spindle PWM must be on PA8 (TIM1_CH1)."
#endif

static spindle_pwm_t spindle_pwm = { .offset = -1 };
static bool pwm_claimed = false;

#define PWM_TIMER   SPINDLE_PWM_TIMER
#define PWM_CCR     (PWM_TIMER->CCR1)
#define PWM_CLOCK   F_APB2          // TIM1 is on APB2, 84 MHz

/* TIM1 CH1: PWM mode 1, preload, up counting. */
static void pwm_config (uint32_t prescaler, uint32_t period, bool inverted)
{
    PWM_TIMER->CR1 &= ~TIM_CR1_CEN;

    PWM_TIMER->PSC = prescaler - 1;
    PWM_TIMER->ARR = period - 1;
    PWM_TIMER->RCR = 0;
    PWM_TIMER->CR1 = TIM_CR1_ARPE;

    PWM_TIMER->CCER &= ~TIM_CCER_CC1E;
    PWM_TIMER->CCMR1 = (PWM_TIMER->CCMR1 & ~(TIM_CCMR1_OC1M | TIM_CCMR1_CC1S)) |
                       TIM_CCMR1_OC1M_2 | TIM_CCMR1_OC1M_1 | TIM_CCMR1_OC1PE;
    PWM_CCR = 0;

    // Off state: outputs keep their idle level when MOE is cleared.
    PWM_TIMER->BDTR |= TIM_BDTR_OSSR | TIM_BDTR_OSSI;

    if(inverted) {
        PWM_TIMER->CCER |= TIM_CCER_CC1P;
        PWM_TIMER->CR2 |= TIM_CR2_OIS1;
    } else {
        PWM_TIMER->CCER &= ~TIM_CCER_CC1P;
        PWM_TIMER->CR2 &= ~TIM_CR2_OIS1;
    }

    PWM_TIMER->EGR = TIM_EGR_UG;
    PWM_TIMER->CCER |= TIM_CCER_CC1E;
    PWM_TIMER->CR1 |= TIM_CR1_CEN;
}

static void pwm_enable (void)
{
    RCC->APB2ENR |= RCC_APB2ENR_TIM1EN;
    (void)RCC->APB2ENR;

    gpio_config(SPINDLE_PWM_PORT, SPINDLE_PWM_PIN, GpioMode_Alternate, false, PullMode_None, SPINDLE_PWM_AF);
}

#endif // SPINDLE_PWM

// Static spindle (off, on cw & on ccw)

inline static void spindle_off (spindle_ptrs_t *spindle)
{
#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
    spindle->context.pwm->flags.enable_out = Off;
  #ifdef SPINDLE_DIRECTION_PIN
    if(spindle->context.pwm->flags.cloned) {
        DIGITAL_OUT(SPINDLE_DIRECTION_PORT, SPINDLE_DIRECTION_PIN, settings.pwm_spindle.invert.ccw);
    } else {
        DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, settings.pwm_spindle.invert.on);
    }
  #else
    DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, settings.pwm_spindle.invert.on);
  #endif
#else
    (void)spindle;
    DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, settings.pwm_spindle.invert.on);
#endif
}

inline static void spindle_on (spindle_ptrs_t *spindle)
{
#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
  #ifdef SPINDLE_DIRECTION_PIN
    if(spindle->context.pwm->flags.cloned) {
        DIGITAL_OUT(SPINDLE_DIRECTION_PORT, SPINDLE_DIRECTION_PIN, !settings.pwm_spindle.invert.ccw);
    } else {
        DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, !settings.pwm_spindle.invert.on);
    }
  #else
    DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, !settings.pwm_spindle.invert.on);
  #endif
    spindle->context.pwm->flags.enable_out = On;
#else
    (void)spindle;
    DIGITAL_OUT(SPINDLE_ENABLE_PORT, SPINDLE_ENABLE_PIN, !settings.pwm_spindle.invert.on);
#endif
}

inline static void spindle_dir (bool ccw)
{
#ifdef SPINDLE_DIRECTION_PIN
    DIGITAL_OUT(SPINDLE_DIRECTION_PORT, SPINDLE_DIRECTION_PIN, ccw ^ settings.pwm_spindle.invert.ccw);
#else
    (void)ccw;
#endif
}

// Start or stop spindle
static void spindleSetState (spindle_ptrs_t *spindle, spindle_state_t state, float rpm)
{
    (void)rpm;

    if(!state.on)
        spindle_off(spindle);
    else {
        spindle_dir(state.ccw);
        spindle_on(spindle);
    }
}

#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM

static void pwm_off (spindle_ptrs_t *spindle)
{
    if(spindle->context.pwm->flags.always_on) {
        PWM_CCR = spindle->context.pwm->off_value;
        PWM_TIMER->BDTR |= TIM_BDTR_MOE;
    } else
        PWM_TIMER->BDTR &= ~TIM_BDTR_MOE;
}

// Sets spindle speed
static void spindleSetSpeed (spindle_ptrs_t *spindle, uint_fast16_t pwm_value)
{
    if(pwm_value == spindle->context.pwm->off_value) {

        if(spindle->context.pwm->flags.rpm_controlled) {
            spindle_off(spindle);
            if(spindle->context.pwm->flags.laser_off_overdrive)
                PWM_CCR = spindle->context.pwm->pwm_overdrive;
        } else
            pwm_off(spindle);

    } else {

        if(!spindle->context.pwm->flags.enable_out && spindle->context.pwm->flags.rpm_controlled)
            spindle_on(spindle);

        PWM_CCR = pwm_value;
        PWM_TIMER->BDTR |= TIM_BDTR_MOE;
    }
}

static uint_fast16_t spindleGetPWM (spindle_ptrs_t *spindle, float rpm)
{
    return spindle->context.pwm->compute_value(spindle->context.pwm, rpm, false);
}

// Start or stop spindle
static void spindleSetStateVariable (spindle_ptrs_t *spindle, spindle_state_t state, float rpm)
{
    if(!(spindle->context.pwm->flags.cloned ? state.ccw : state.on)) {
        spindle_off(spindle);
        pwm_off(spindle);
    } else {
#ifdef SPINDLE_DIRECTION_PIN
        if(!spindle->context.pwm->flags.cloned)
            spindle_dir(state.ccw);
#endif
        if(rpm == 0.0f && spindle->context.pwm->flags.rpm_controlled)
            spindle_off(spindle);
        else {
            spindle_on(spindle);
            spindleSetSpeed(spindle, spindle->context.pwm->compute_value(spindle->context.pwm, rpm, false));
        }
    }
}

static bool spindleConfig (spindle_ptrs_t *spindle)
{
    if(spindle == NULL)
        return false;

    if(pwm_claimed) {

        uint32_t prescaler = 1;

        if(spindle_precompute_pwm_values(spindle, &spindle_pwm, &settings.pwm_spindle, PWM_CLOCK / prescaler)) {

            while(spindle_pwm.period > 65534) {
                prescaler++;
                spindle_precompute_pwm_values(spindle, &spindle_pwm, &settings.pwm_spindle, PWM_CLOCK / prescaler);
            }

            pwm_config(prescaler, spindle_pwm.period, spindle_pwm.flags.invert_pwm);

            spindle_pwm.flags.invert_pwm = Off; // Handled in hardware
            spindle->set_state = spindleSetStateVariable;

        } else {
            if(spindle->context.pwm->flags.enable_out)
                spindle->set_state(spindle, (spindle_state_t){0}, 0.0f);
            spindle->set_state = spindleSetState;
        }
    } else // PWM output not available
        spindle->set_state = spindleSetState;

    spindle_update_caps(spindle, spindle->cap.variable ? &spindle_pwm : NULL);

    return true;
}

#endif // SPINDLE_PWM

// Returns spindle state in a spindle_state_t variable
static spindle_state_t spindleGetState (spindle_ptrs_t *spindle)
{
    spindle_state_t state = { settings.pwm_spindle.invert.mask };

#ifdef SPINDLE_ENABLE_PIN
    state.on = (SPINDLE_ENABLE_PORT->ODR >> SPINDLE_ENABLE_PIN) & 1U;
#endif
#ifdef SPINDLE_DIRECTION_PIN
    state.ccw = (SPINDLE_DIRECTION_PORT->ODR >> SPINDLE_DIRECTION_PIN) & 1U;
#endif
    state.value ^= settings.pwm_spindle.invert.mask;
#ifdef SPINDLE_PWM_PIN
    state.on |= spindle->param->state.on;
#else
    (void)spindle;
#endif

    return state;
}

// Configures the spindle when the settings change
static void settingsChanged (settings_t *settings, settings_changed_flags_t changed)
{
    on_settings_changed(settings, changed);

#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
    if(changed.spindle) {
        spindleConfig(spindle_get_hal(spindle_id, SpindleHAL_Configured));
        if(spindle_id == spindle_get_default())
            spindle_select(spindle_id);
    }
#else
    (void)settings;
#endif
}

#endif // DRIVER_SPINDLE_ENABLE

// Claims the spindle and coolant outputs from the aux output pool.
bool aux_out_claim_explicit (aux_ctrl_out_t *aux_ctrl)
{
    xbar_t *pin;

#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
    if(aux_ctrl->function == Output_SpindlePWM) {
        if(aux_ctrl->gpio.port != (void *)SPINDLE_PWM_PORT || aux_ctrl->gpio.pin != SPINDLE_PWM_PIN) {
            aux_ctrl->port = IOPORT_UNASSIGNED;
            return false;
        }
    }
#endif

    if((pin = ioport_claim(Port_Digital, Port_Output, &aux_ctrl->port, NULL))) {
        ioport_set_function(pin, aux_ctrl->function, NULL);
#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
        if(aux_ctrl->function == Output_SpindlePWM) {
            ((output_signal_t *)aux_ctrl->output)->mode.pwm = On;
            pwm_enable();
            pwm_claimed = true;
        }
#endif
    } else
        aux_ctrl->port = IOPORT_UNASSIGNED;

    return aux_ctrl->port != IOPORT_UNASSIGNED;
}

void driver_spindles_init (void)
{
#if DRIVER_SPINDLE_ENABLE && defined(SPINDLE_ENABLE_PIN)

 #if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM

    static const spindle_ptrs_t spindle = {
        .type = SpindleType_PWM,
  #if DRIVER_SPINDLE_ENABLE & SPINDLE_DIR
        .ref_id = SPINDLE_PWM0,
  #else
        .ref_id = SPINDLE_PWM0_NODIR,
  #endif
        .config = spindleConfig,
        .set_state = spindleSetStateVariable,
        .get_state = spindleGetState,
        .get_pwm = spindleGetPWM,
        .update_pwm = spindleSetSpeed,
        .cap = {
            .gpio_controlled = On,
            .variable = On,
            .laser = On,
            .pwm_invert = On,
  #if DRIVER_SPINDLE_ENABLE & SPINDLE_DIR
            .direction = On
  #endif
        }
    };

    if(!(pwm_claimed && (spindle_id = spindle_register(&spindle, DRIVER_SPINDLE_NAME)) != -1))
        task_run_on_startup(report_warning, "PWM spindle failed to initialize!");

 #else

    static const spindle_ptrs_t spindle = {
        .type = SpindleType_Basic,
  #if DRIVER_SPINDLE_ENABLE & SPINDLE_DIR
        .ref_id = SPINDLE_ONOFF0_DIR,
  #else
        .ref_id = SPINDLE_ONOFF0,
  #endif
        .set_state = spindleSetState,
        .get_state = spindleGetState,
        .cap = {
            .gpio_controlled = On,
  #if DRIVER_SPINDLE_ENABLE & SPINDLE_DIR
            .direction = On
  #endif
        }
    };

    spindle_id = spindle_register(&spindle, DRIVER_SPINDLE_NAME);

 #endif

    on_settings_changed = grbl.on_settings_changed;
    grbl.on_settings_changed = settingsChanged;

#endif // DRIVER_SPINDLE_ENABLE
}
