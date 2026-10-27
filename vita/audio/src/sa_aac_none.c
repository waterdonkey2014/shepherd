/* No platform AAC decoder (portable builds): aac-adts takes play silent with correct timing/state. */
#include "sa_internal.h"
const sa_aac_vtbl *sa_platform_aac(void) { return NULL; }
