/*
  main.c - grblHAL startup for the STM32F401 Black Pill, register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.
*/

#include "driver.h"
#include "grbl/grbllib.h"

int main (void)
{
    // $DFU leaves a marker in the RTC backup domain: jump into the ST
    // bootloader before anything is set up.
    cpu_check_bootloader();

    cpu_init();

    // 1 ms system tick, see driver_systick().
    SysTick->LOAD = SystemCoreClock / 1000UL - 1UL;
    SysTick->VAL = 0;
    NVIC_SetPriority(SysTick_IRQn, IRQ_PRIO_SYSTICK);
    SysTick->CTRL = SysTick_CTRL_CLKSOURCE_Msk | SysTick_CTRL_TICKINT_Msk | SysTick_CTRL_ENABLE_Msk;

    __enable_irq();

    // Never returns: the core calls driver_init(), loads the settings,
    // calls hal.driver_setup() and runs the protocol loop.
    grbl_enter();

    for(;;);
}
