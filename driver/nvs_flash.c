/*
  nvs_flash.c - settings storage in internal flash, register level.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  The core keeps a RAM copy of the whole non volatile storage (settings,
  coordinate systems, startup lines, tool table) and calls
  memcpy_to_flash() to write it back after a change.

  Storage: flash sector 1, 16 KB at 0x08004000. The linker files
  (cmsis/linker/stm32f401x?_flash.icf, test/gcc/stm32f4.ld) keep this sector free of code.
  Sector 0 holds the vector table, so the image is not contiguous.

  Writing erases the sector (16 KB, about 0.3 s, the CPU stalls on flash
  reads meanwhile), then programs the data word by word. The core only
  saves settings while the machine is idle.

  grblHAL is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.
*/

#include <string.h>

#include "driver.h"

#if FLASH_ENABLE

#define NVS_FLASH_ADDR      0x08004000UL
#define NVS_FLASH_SECTOR    1UL
#define NVS_FLASH_SIZE      0x4000UL

#define FLASH_KEY1          0x45670123UL
#define FLASH_KEY2          0xCDEF89ABUL

#define FLASH_SR_ERRORS     (FLASH_SR_PGSERR | FLASH_SR_PGPERR | FLASH_SR_PGAERR | FLASH_SR_WRPERR | FLASH_SR_OPERR)

/* Flash operations: the caches must not hold stale data afterwards. */
static void flash_wait (void)
{
    while(FLASH->SR & FLASH_SR_BSY);
}

static bool flash_unlock (void)
{
    if(FLASH->CR & FLASH_CR_LOCK) {
        FLASH->KEYR = FLASH_KEY1;
        FLASH->KEYR = FLASH_KEY2;
    }

    return !(FLASH->CR & FLASH_CR_LOCK);
}

static void flash_lock (void)
{
    FLASH->CR |= FLASH_CR_LOCK;
}

static bool flash_erase_sector (uint32_t sector)
{
    flash_wait();
    FLASH->SR = FLASH_SR_ERRORS | FLASH_SR_EOP;

    // x32 parallelism (2.7..3.6 V supply).
    FLASH->CR = (FLASH->CR & ~(FLASH_CR_PSIZE | FLASH_CR_SNB)) | FLASH_CR_PSIZE_1 |
                (sector << FLASH_CR_SNB_Pos) | FLASH_CR_SER;
    FLASH->CR |= FLASH_CR_STRT;
    flash_wait();
    FLASH->CR &= ~(FLASH_CR_SER | FLASH_CR_SNB);

    return !(FLASH->SR & FLASH_SR_ERRORS);
}

static bool flash_program (uint32_t address, const uint8_t *data, uint32_t length)
{
    bool ok = true;

    flash_wait();
    FLASH->SR = FLASH_SR_ERRORS | FLASH_SR_EOP;
    FLASH->CR = (FLASH->CR & ~FLASH_CR_PSIZE) | FLASH_CR_PSIZE_1 | FLASH_CR_PG;

    while(length && ok) {

        uint32_t word = 0xFFFFFFFFUL;
        uint32_t n = length < 4 ? length : 4;

        memcpy(&word, data, n);
        *(volatile uint32_t *)address = word;
        flash_wait();
        ok = !(FLASH->SR & FLASH_SR_ERRORS);

        address += 4;
        data += n;
        length -= n;
    }

    FLASH->CR &= ~FLASH_CR_PG;

    return ok;
}

static void flash_flush_caches (void)
{
    uint32_t acr = FLASH->ACR;

    FLASH->ACR = acr & ~(FLASH_ACR_ICEN | FLASH_ACR_DCEN);
    FLASH->ACR = (acr & ~(FLASH_ACR_ICEN | FLASH_ACR_DCEN)) | FLASH_ACR_ICRST | FLASH_ACR_DCRST;
    FLASH->ACR = acr;
}

bool memcpy_from_flash (uint8_t *dest)
{
    memcpy(dest, (const void *)NVS_FLASH_ADDR, hal.nvs.size);

    return true;
}

bool memcpy_to_flash (uint8_t *source)
{
    bool ok;

    if(hal.nvs.size > NVS_FLASH_SIZE)
        return false;

    if(!memcmp(source, (const void *)NVS_FLASH_ADDR, hal.nvs.size))
        return true;

    if(!flash_unlock())
        return false;

    // Retry the erase once if it fails (as in the grblHAL STM32F4xx driver).
    if(!(ok = flash_erase_sector(NVS_FLASH_SECTOR)))
        ok = flash_erase_sector(NVS_FLASH_SECTOR);

    if(ok)
        ok = flash_program(NVS_FLASH_ADDR, source, hal.nvs.size);

    flash_lock();
    flash_flush_caches();

    return ok && !memcmp(source, (const void *)NVS_FLASH_ADDR, hal.nvs.size);
}

#endif // FLASH_ENABLE
