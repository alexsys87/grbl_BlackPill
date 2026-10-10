/* GCC/newlib heap support. Do not let malloc grow into the reserved stack.
 * The linker reserves the same 12 KiB heap as the IAR projects.
 * Other bare-metal syscall stubs come from --specs=nosys.specs.
 */
#include <errno.h>
#include <stddef.h>
#include <stdint.h>

extern unsigned char __HeapBase, __HeapLimit;

void *_sbrk(ptrdiff_t increment)
{
    static unsigned char *current;
    if(current == NULL)
        current = &__HeapBase;

    uintptr_t position = (uintptr_t)current;
    uintptr_t base = (uintptr_t)&__HeapBase;
    uintptr_t limit = (uintptr_t)&__HeapLimit;

    if((increment >= 0 && (uintptr_t)increment > limit - position) ||
       (increment < 0 && (uintptr_t)(-(increment + 1)) + 1 > position - base)) {
        errno = ENOMEM;
        return (void *)-1;
    }

    void *previous = current;
    current = (unsigned char *)(position + increment);
    return previous;
}
