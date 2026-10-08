/*
  cnc3018_blackpill_map.h - pin map of the CNC 3018 on a WeAct Studio
                            STM32F401CC/CE "Black Pill" (V3.x).

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  Pin assignments (based on the grblHAL "BlackPill" map, the ganged / 4th
  axis motor is dropped, USART1 and coolant moved off the 32 kHz crystal,
  Y step / dir on PA6 / PA7 to free PA2 / PA3 for USART2):

                                 -----------
                             VB |           | +3V
                  LED  PC13 C13 |           | GND
                            C14 |           | +5V
                            C15 | *     - * | B9   Safety door (option)
                            RST |      |K|  | B8   Cycle start
                  X step     A0 |       -   | B7   Feed hold
                  X dir      A1 |           | B6   Reset / E-stop
          USART2 TX (WiFi)   A2 |           | B5   Coolant mist (M7)
          USART2 RX (WiFi)   A3 |    / \    | B4   Coolant flood (M8)
                  Z step     A4 |   <MCU>   | B3
                  Z dir      A5 |    \ /    | A15
                  Y step     A6 |           | A12  USB D+
                  Y dir      A7 |   -   -   | A11  USB D-
       Stepper enable (EN)   B0 |  |R| |B|  | A10  USART1 RX (WiFi / BT)
       Spindle enable        B1 |   -   -   | A9   USART1 TX (WiFi / BT)
       Spindle direction     B2 |           | A8   Spindle PWM (TIM1_CH1)
                            B10 |           | B15  Probe
                            +3V |   -----   | B14  Z limit
                            GND |  |     |  | B13  Y limit
                            +5V |  | USB |  | B12  X limit
                                 -----------

  - PA0 is also the KEY button of the board: don't press it while running.
  - Inputs are 3.3 V with internal pull-ups, switches to GND (NO). All
    input pins used here (PB6..PB9, PB12..PB15) are 5 V tolerant.
  - Step / dir / enable outputs are 3.3 V push-pull. Most 3018 driver
    boards (A4988, DRV8825, TMC2208) accept 3.3 V logic.
  - PB2 is BOOT1, it may be used as an output after reset.
  - USART1 / USART2 are 3.3 V (ESP-01, ESP32, HC-05 connect directly):
    module TX to the RX pin, module RX to the TX pin, common GND.
*/

#if N_ABC_MOTORS > 0
#error "The CNC 3018 BlackPill map supports 3 axes only!"
#endif

#define BOARD_NAME              "CNC 3018 BlackPill"
#define BOARD_URL               "https://github.com/alexsys87/grbl_BlackPill"

// Step pulse outputs, GPIOA.
#define STEP_PORT               GPIOA
#define X_STEP_PIN              0
#define Y_STEP_PIN              6
#define Z_STEP_PIN              4
#define STEP_OUTMODE            GPIO_MAP

// Direction outputs, GPIOA.
#define DIRECTION_PORT          GPIOA
#define X_DIRECTION_PIN         1
#define Y_DIRECTION_PIN         7
#define Z_DIRECTION_PIN         5
#define DIRECTION_OUTMODE       GPIO_MAP

// Common stepper driver enable output.
#define STEPPERS_ENABLE_PORT    GPIOB
#define STEPPERS_ENABLE_PIN     0

// Limit switch inputs, three consecutive pins.
#define LIMIT_PORT              GPIOB
#define X_LIMIT_PIN             12
#define Y_LIMIT_PIN             13
#define Z_LIMIT_PIN             14
#define LIMIT_INMODE            GPIO_SHIFT12

// Auxiliary outputs, claimed by the spindle and coolant below. Any
// output left unclaimed is available for M62..M65.
#define AUXOUTPUT0_PORT         GPIOA // Spindle PWM
#define AUXOUTPUT0_PIN          8
#define AUXOUTPUT1_PORT         GPIOB // Spindle direction
#define AUXOUTPUT1_PIN          2
#define AUXOUTPUT2_PORT         GPIOB // Spindle enable
#define AUXOUTPUT2_PIN          1
#define AUXOUTPUT3_PORT         GPIOB // Coolant flood
#define AUXOUTPUT3_PIN          4
#define AUXOUTPUT4_PORT         GPIOB // Coolant mist
#define AUXOUTPUT4_PIN          5

#if DRIVER_SPINDLE_ENABLE & SPINDLE_ENA
#define SPINDLE_ENABLE_PORT     AUXOUTPUT2_PORT
#define SPINDLE_ENABLE_PIN      AUXOUTPUT2_PIN
#endif
#if DRIVER_SPINDLE_ENABLE & SPINDLE_PWM
#define SPINDLE_PWM_PORT        AUXOUTPUT0_PORT
#define SPINDLE_PWM_PIN         AUXOUTPUT0_PIN
#endif
#if DRIVER_SPINDLE_ENABLE & SPINDLE_DIR
#define SPINDLE_DIRECTION_PORT  AUXOUTPUT1_PORT
#define SPINDLE_DIRECTION_PIN   AUXOUTPUT1_PIN
#endif

#if COOLANT_ENABLE & COOLANT_FLOOD
#define COOLANT_FLOOD_PORT      AUXOUTPUT3_PORT
#define COOLANT_FLOOD_PIN       AUXOUTPUT3_PIN
#endif
#if COOLANT_ENABLE & COOLANT_MIST
#define COOLANT_MIST_PORT       AUXOUTPUT4_PORT
#define COOLANT_MIST_PIN        AUXOUTPUT4_PIN
#endif

// Auxiliary inputs. Control signals and the probe are bound to them,
// unused ones are available for M66.
#define AUXINPUT0_PORT          GPIOB // Safety door
#define AUXINPUT0_PIN           9
#define AUXINPUT1_PORT          GPIOB // Probe
#define AUXINPUT1_PIN           15
#define AUXINPUT2_PORT          GPIOB // Reset / E-stop
#define AUXINPUT2_PIN           6
#define AUXINPUT3_PORT          GPIOB // Feed hold
#define AUXINPUT3_PIN           7
#define AUXINPUT4_PORT          GPIOB // Cycle start
#define AUXINPUT4_PIN           8

#if CONTROL_ENABLE & CONTROL_HALT
#define RESET_PORT              AUXINPUT2_PORT
#define RESET_PIN               AUXINPUT2_PIN
#endif
#if CONTROL_ENABLE & CONTROL_FEED_HOLD
#define FEED_HOLD_PORT          AUXINPUT3_PORT
#define FEED_HOLD_PIN           AUXINPUT3_PIN
#endif
#if CONTROL_ENABLE & CONTROL_CYCLE_START
#define CYCLE_START_PORT        AUXINPUT4_PORT
#define CYCLE_START_PIN         AUXINPUT4_PIN
#endif

#if PROBE_ENABLE
#define PROBE_PORT              AUXINPUT1_PORT
#define PROBE_PIN               AUXINPUT1_PIN
#endif

#if SAFETY_DOOR_ENABLE
#define SAFETY_DOOR_PORT        AUXINPUT0_PORT
#define SAFETY_DOOR_PIN         AUXINPUT0_PIN
#endif

// USART1: the host port without USB, else a port in parallel with USB.
#define UART1_PORT              GPIOA
#define UART1_TX_PIN            9
#define UART1_RX_PIN            10

// USART2: a port in parallel with the host port (e.g. a WiFi module).
#define UART2_PORT              GPIOA
#define UART2_TX_PIN            2
#define UART2_RX_PIN            3

// Status LED of the Black Pill (active low): on while the controller runs.
#define LED_PORT                GPIOC
#define LED_PIN                 13

/* EOF */
