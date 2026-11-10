/* SHEPHERD-VITA device kit #13, track bootc: the AAC decoder check's MAC side (the reference) -- sa_deccheck.h's driver over AudioToolbox
 * (sa_aac_apple.c, the decoder the bake's verify listened to). Reads "<file>\t<channels>\t<rate>" lines on stdin (files relative to argv[1], the
 * bake's folder) and prints "<file>\t<channels>\t<rate>\t<frames>\t<fnv64 hex>\t<peak>\t<access units>\t<refused>" per take.
 * Built and driven by vita/tools/deccheck_aac_ref.py (clang + vita/audio/build/mac/libshepaudio.a + AudioToolbox). Inputs / outputs owner-local. */
#include "sa_deccheck.h"

int main (int argc, char **argv)
{
    if (argc != 2) { fprintf (stderr, "usage: sa_deccheck <bake dir> < takes.tsv\n"); return 2; }
    const sa_aac_vtbl *v = sa_platform_aac ();
    if (!v || (v->lib_init && v->lib_init (1) != 0)) { fprintf (stderr, "no AAC decoder\n"); return 1; }
    static int16_t pcm [8192 * 2];
    char line [1024];
    int rc = 0;
    while (fgets (line, sizeof line, stdin)) {
        char file [900]; int ch = 0, rate = 0;
        if (sscanf (line, "%899[^\t]\t%d\t%d", file, &ch, &rate) != 3) continue;
        char path [1024];
        snprintf (path, sizeof path, "%s/%s", argv [1], file);
        FILE *f = fopen (path, "rb");
        if (!f) { fprintf (stderr, "missing %s\n", path); rc = 1; continue; }
        fseek (f, 0, SEEK_END); long n = ftell (f); fseek (f, 0, SEEK_SET);
        unsigned char *b = (unsigned char *) malloc ((size_t) n);
        if (!b || fread (b, 1, (size_t) n, f) != (size_t) n) { fprintf (stderr, "unreadable %s\n", path); fclose (f); free (b); rc = 1; continue; }
        fclose (f);
        sa_dec_res r;
        if (sa_deccheck_buf (v, b, (int) n, ch, rate, &r, pcm, 8192, NULL, NULL) < 0) { fprintf (stderr, "decoder did not open for %s\n", path); rc = 1; }
        else printf ("%s\t%d\t%d\t%u\t%016llx\t%u\t%u\t%u\n", file, ch, rate, r.frames, r.fnv, r.peak, r.aus, r.refused);
        free (b);
    }
    if (v->lib_term) v->lib_term ();
    return rc;
}
