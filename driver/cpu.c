/*
  cpu.c - clock setup and fault handling for STM32F401, register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).
  Taken over from Teacup_Firmware_iar (hal/cpu.c).

  Sets up the PLL from the board crystal (HSE_CLOCK_HZ) to run at 84 MHz
  with a 48 MHz USB clock, with automatic fallback to the internal 16 MHz
  oscillator. Also starts the DWT cycle counter (delays, microsecond time
  base), sets the interrupt priority grouping and enables the FPU lazy
  stacking.

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.
*/

#include "driver.h"

cpu_clock_t cpu_clock_source = CpuClock_Failed;

/* PLL: VCO input is always 1 MHz, VCO = 336 MHz, SYSCLK = VCO / 4 = 84 MHz,
   USB = VCO / 7 = 48 MHz. */
#define PLL_P       4
#define PLL_Q       7
#define PLL_VCO_MHZ ((F_CPU / 1000000UL) * PLL_P)

#if (HSE_CLOCK_HZ % 1000000UL) != 0 || HSE_CLOCK_HZ < 4000000UL || HSE_CLOCK_HZ > 26000000UL
  #error HSE_CLOCK_HZ must be an integer number of MHz between 4 and 26.
#endif

#define PLLCFGR_VALUE(m, src) ((uint32_t)(m) | \
          ((uint32_t)PLL_VCO_MHZ << RCC_PLLCFGR_PLLN_Pos) | \
          ((uint32_t)(PLL_P / 2 - 1) << RCC_PLLCFGR_PLLP_Pos) | \
          (src) | \
          ((uint32_t)PLL_Q << RCC_PLLCFGR_PLLQ_Pos))

/* Flash wait states for 2.7..3.6 V supply, RM0368 table 6: 84 MHz -> 2. */
#define FLASH_WS    ((F_CPU - 1) / 30000000UL)

/// RTC backup register 0 value asking for the bootloader.
#define BOOTLOADER_MAGIC  0xDF00B007UL
/// STM32F401 system memory: bootloader vector table.
#define SYSMEM_BASE       0x1FFF0000UL

/** Wait for a flag with timeout. \return true if the flag came up. */
static bool wait_flag (volatile uint32_t *reg, uint32_t mask, uint32_t value, uint32_t timeout)
{
    while(timeout--) {
        if((*reg & mask) == value)
            return true;
    }

    return false;
}

/*
  $DFU: reboot into the STM32 system bootloader (USB DFU). Sets a marker
  in RTC backup register 0 and resets; cpu_check_bootloader() at the next
  start jumps into the bootloader before the clocks are set up.
*/
void cpu_reboot_to_bootloader (void)
{
    RCC->APB1ENR |= RCC_APB1ENR_PWREN;
    (void)RCC->APB1ENR;
    PWR->CR |= PWR_CR_DBP;                  // Backup domain writable.
    RTC->BKP0R = BOOTLOADER_MAGIC;
    NVIC_SystemReset();
    for(;;);
}

/// Very first thing at startup: enter the bootloader if $DFU asked for it.
void cpu_check_bootloader (void)
{
    RCC->APB1ENR |= RCC_APB1ENR_PWREN;
    (void)RCC->APB1ENR;
    if(RTC->BKP0R != BOOTLOADER_MAGIC)
        return;
    PWR->CR |= PWR_CR_DBP;
    RTC->BKP0R = 0;                         // Next reset starts the firmware.

    // Reset state otherwise (HSI clock, no peripherals, no interrupts
    // enabled): system memory at address 0, its stack and reset vector.
    RCC->APB2ENR |= RCC_APB2ENR_SYSCFGEN;
    (void)RCC->APB2ENR;
    SYSCFG->MEMRMP = SYSCFG_MEMRMP_MEM_MODE_0;
    SCB->VTOR = SYSMEM_BASE;
    __set_MSP(*(volatile uint32_t *)SYSMEM_BASE);
    __enable_irq();
    // Read the reset vector after switching the stack, no local variables.
    ((void (*)(void))(*(volatile uint32_t *)(SYSMEM_BASE + 4)))();
}

void cpu_init (void)
{
    // Make sure we run from HSI with the PLL off, e.g. after a bootloader.
    RCC->CR |= RCC_CR_HSION;
    wait_flag(&RCC->CR, RCC_CR_HSIRDY, RCC_CR_HSIRDY, 100000);
    RCC->CFGR &= ~RCC_CFGR_SW;
    wait_flag(&RCC->CFGR, RCC_CFGR_SWS, RCC_CFGR_SWS_HSI, 100000);
    RCC->CR &= ~RCC_CR_PLLON;
    wait_flag(&RCC->CR, RCC_CR_PLLRDY, 0, 100000);

    // Regulator voltage scale 2 (up to 84 MHz).
    RCC->APB1ENR |= RCC_APB1ENR_PWREN;
    (void)RCC->APB1ENR;
    PWR->CR = (PWR->CR & ~PWR_CR_VOS) | PWR_CR_VOS_1;

    // Crystal, with fallback to the internal RC oscillator.
    RCC->CR |= RCC_CR_HSEON;
    if(wait_flag(&RCC->CR, RCC_CR_HSERDY, RCC_CR_HSERDY, 500000)) {
        RCC->PLLCFGR = PLLCFGR_VALUE(HSE_CLOCK_HZ / 1000000UL, RCC_PLLCFGR_PLLSRC_HSE);
        cpu_clock_source = CpuClock_HSE;
    } else {
        RCC->CR &= ~RCC_CR_HSEON;
        RCC->PLLCFGR = PLLCFGR_VALUE(16, RCC_PLLCFGR_PLLSRC_HSI);
        cpu_clock_source = CpuClock_HSI;
    }

    RCC->CR |= RCC_CR_PLLON;
    if(wait_flag(&RCC->CR, RCC_CR_PLLRDY, RCC_CR_PLLRDY, 500000)) {
        // Flash wait states first, then caches and prefetch.
        FLASH->ACR = FLASH_ACR_ICRST | FLASH_ACR_DCRST;
        FLASH->ACR = FLASH_ACR_PRFTEN | FLASH_ACR_ICEN | FLASH_ACR_DCEN |
                     (FLASH_WS << FLASH_ACR_LATENCY_Pos);
        wait_flag(&FLASH->ACR, FLASH_ACR_LATENCY, FLASH_WS << FLASH_ACR_LATENCY_Pos, 100000);

        // AHB = 84 MHz, APB1 = 42 MHz (max), APB2 = 84 MHz.
        RCC->CFGR = (RCC->CFGR & ~(RCC_CFGR_HPRE | RCC_CFGR_PPRE1 | RCC_CFGR_PPRE2))
                    | RCC_CFGR_HPRE_DIV1 | RCC_CFGR_PPRE1_DIV2 | RCC_CFGR_PPRE2_DIV1;
        RCC->CFGR = (RCC->CFGR & ~RCC_CFGR_SW) | RCC_CFGR_SW_PLL;
        wait_flag(&RCC->CFGR, RCC_CFGR_SWS, RCC_CFGR_SWS_PLL, 100000);
        SystemCoreClock = F_CPU;
    } else {
        cpu_clock_source = CpuClock_Failed;
        SystemCoreClock = 16000000UL;
    }

    // GPIO clocks.
    RCC->AHB1ENR |= RCC_AHB1ENR_GPIOAEN | RCC_AHB1ENR_GPIOBEN | RCC_AHB1ENR_GPIOCEN | RCC_AHB1ENR_GPIOHEN;
    (void)RCC->AHB1ENR;
    RCC->APB2ENR |= RCC_APB2ENR_SYSCFGEN;   // EXTI line mapping.
    (void)RCC->APB2ENR;

    // Cycle counter for delays and the microsecond time base.
    CoreDebug->DEMCR |= CoreDebug_DEMCR_TRCENA_Msk;
    DWT->CYCCNT = 0;
    DWT->CTRL |= DWT_CTRL_CYCCNTENA_Msk;

    // FPU: automatic and lazy context saving. An interrupt which doesn't
    // use the FPU doesn't pay for stacking the FPU registers.
    FPU->FPCCR |= FPU_FPCCR_ASPEN_Msk | FPU_FPCCR_LSPEN_Msk;

    // 4 bits preemption priority, no subpriority.
    NVIC_SetPriorityGrouping(3);

    // Keep the step timer frozen while halted in the debugger.
    DBGMCU->APB1FZ |= DBGMCU_APB1_FZ_DBG_TIM5_STOP;
}

/// Busy wait, works with interrupts disabled.
void delay_us (uint32_t us)
{
    uint32_t start = DWT->CYCCNT, cycles = us * (SystemCoreClock / 1000000UL);

    while(DWT->CYCCNT - start < cycles);
}

/*
  Fault handlers. A fault could leave the spindle PWM or the step outputs
  running. Resetting returns all pins to their reset state (inputs, the
  spindle and the stepper drivers switch off).
  Define DEBUG_FAULT_HALT to stop in the handler for debugging instead.
*/
static void fault (void)
{
#ifdef DEBUG_FAULT_HALT
    __disable_irq();
    for(;;);
#else
    NVIC_SystemReset();
#endif
}

void HardFault_Handler (void);
void MemManage_Handler (void);
void BusFault_Handler (void);
void UsageFault_Handler (void);

void HardFault_Handler (void)  { fault(); }
void MemManage_Handler (void)  { fault(); }
void BusFault_Handler (void)   { fault(); }
void UsageFault_Handler (void) { fault(); }
