/*
  compat.c - C library functions missing from IAR DLIB.

  Part of grbl_BlackPill (grblHAL driver, register level, IAR EWARM).

  The grblHAL core uses the POSIX function strncasecmp(). newlib (GCC
  builds) has it, IAR DLIB does not.
*/

#include <stddef.h>
#include <ctype.h>

#if defined(__ICCARM__)

int strncasecmp (const char *s1, const char *s2, size_t n)
{
    while(n--) {
        int c1 = tolower((unsigned char)*s1++), c2 = tolower((unsigned char)*s2++);

        if(c1 != c2)
            return c1 - c2;
        if(c1 == '\0')
            break;
    }

    return 0;
}

#else

// ISO C forbids an empty translation unit.
typedef int compat_unused_t;

#endif
