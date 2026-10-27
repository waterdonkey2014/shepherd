/* sa_check: load-and-play check of a real bake set (silent; numbers only).
 *   sa_check --manifest runtime/audiobake/<set>/manifest.json [--wav OUT.wav] [--cap-ms 6000] [--batch 16]
 * 1. every cached take loads (container, frames vs manifest); every AAC take decodes (frames after encoderDelay vs manifest)
 * 2. every event is started (batches of --batch), one-shots must reach STOPPED on time, held events are stopped at the cap.
 *    "On time" = FMOD's STOPPED for that take (the bake's stopFrame) when the manifest has it, else the take's end; a one-shot
 *    that starts in its silent group (silentAtDefault) is due at silentStopFrame / lengthMs
 * Derived game data: --wav is only accepted under the set's runtime/audiobake/ directory. Never played. */
#include "sa_internal.h"

static double now_s(void) { return sa_now(); }

int main(int argc, char **argv)
{
    const char *mpath = NULL, *wav = NULL; int cap_ms = 6000, batch = 16, parse_only = 0;
    for (int k = 1; k < argc; k++) {
        if (!strcmp(argv[k], "--manifest") && k + 1 < argc) mpath = argv[++k];
        else if (!strcmp(argv[k], "--wav") && k + 1 < argc) wav = argv[++k];
        else if (!strcmp(argv[k], "--cap-ms") && k + 1 < argc) cap_ms = atoi(argv[++k]);
        else if (!strcmp(argv[k], "--batch") && k + 1 < argc) batch = atoi(argv[++k]);
        else if (!strcmp(argv[k], "--parse-only")) parse_only = 1;
        else { fprintf(stderr, "unknown arg %s\n", argv[k]); return 2; }
    }
    if (!mpath) { fprintf(stderr, "usage: sa_check --manifest M [--wav OUT.wav] [--cap-ms N] [--batch N]\n"); return 2; }
    if (wav) {
        const char *root = strstr(mpath, "runtime/audiobake/");
        if (!root || !strstr(wav, "runtime/audiobake/")) { fprintf(stderr, "refusing --wav outside runtime/audiobake/ (derived game data)\n"); return 2; }
    }
    sa_config c; sa_config_defaults(&c);
    c.flags = SA_FLAG_SYNC_IO; c.max_instances = 4096; c.max_voices = 48;
    double t0 = now_s();
    sa_system *s = sa_init_ex(mpath, &c);
    if (!s) { fprintf(stderr, "init: %s\n", sa_last_error()); return 1; }
    printf("manifest: %d events (+snapshots), %d takes, %d buses, %d params, %d snapshot curve points; parse+tables %.1f ms\n",
           s->nevents, s->ntakes, s->nbuses, s->nparams, s->ncurvepts, (now_s() - t0) * 1e3);
    {
        int n3d = 0, nsp = 0, nrel = 0, nsil = 0, nsf = 0;
        for (int e = 0; e < s->nevents; e++) { const sa_event *ev = &s->events[e]; if (ev->is3d) { n3d++; if (ev->spatial >= 0) nsp++; } if (ev->nrelpt) nrel++; if (ev->silent_group >= 0) nsil++; }
        for (int t = 0; t < s->ntakes; t++) if (s->takes[t].stop_frame >= 0) nsf++;
        printf("bake fields: stopFrame on %d takes; measured 3D model on %d of %d 3D events (%d distinct); release curve on %d events; silent group on %d\n",
               nsf, nsp, n3d, s->nspatials, nrel, nsil);
    }
    {   /* resident tables on the Vita (arm32 struct sizes measured with arm-vita-eabi-gcc: event 128, take 52, entry 64,
           group 16, param 32, bus 36, spatial 232, instance 364, voice 296, voice mirror 56, cmd 64, stream 228) */
        size_t tables = (size_t)s->nevents * 128 + (size_t)s->ntakes * (52 + 4 + 64) + (size_t)s->ngroups * 16 + (size_t)s->nparams * 32 +
                        (size_t)s->nbuses * 36 + (size_t)s->nbusvols * 12 + (size_t)s->ncurvepts * 16 + (size_t)s->npvals * 4 +
                        (size_t)s->nspatials * 232 + (size_t)s->nrelpts * 8 +
                        (size_t)(s->h_event_mask + 1) * 8 + (size_t)(s->h_bus_mask + 1) * 4 + s->strpool_len;
        size_t fixed = (size_t)c.max_instances * (364 + 4 + 4 * (size_t)(s->max_event_params ? s->max_event_params : 1)) + (size_t)c.max_voices * (296 + 56) +
                       (size_t)SA_CMDQ_CAP * 64 + 2912 + 32768;
        uint32_t cap = 1024; while (cap < (uint32_t)c.stream_buffer_frames) cap <<= 1;
        size_t streams = (size_t)c.max_streams * (228 + (size_t)cap * 4 + 32768 + 16384);
        printf("Vita RAM estimate: manifest tables %.2f MiB (strings %.2f MiB) + fixed pools %.2f MiB (%d instances, %d voices, %d-cmd queue) + streams %.2f MiB (%d x %u-frame rings) + cache budget %.0f MiB\n",
               tables / 1048576.0, s->strpool_len / 1048576.0, fixed / 1048576.0, c.max_instances, c.max_voices, SA_CMDQ_CAP, streams / 1048576.0,
               c.max_streams, cap, c.cache_budget_bytes / 1048576.0);
    }
    if (parse_only) { sa_shutdown(s); return 0; }
    /* ---- 1. takes ---- */
    int n_cached = 0, n_stream = 0, bad = 0, frames_mismatch = 0, aac_bad = 0;
    size_t bytes_pcm = 0, bytes_adpcm = 0, file_bytes = 0; double tload = 0, taac = 0; double aac_seconds = 0;
    int max_pad = 0, min_pad = 1 << 30;
    const sa_aac_vtbl *aac = sa_platform_aac();
    int16_t *pcm = (int16_t *)malloc(sizeof(int16_t) * 2 * 4096);
    for (int t = 0; t < s->ntakes; t++) {
        const sa_take *tk = &s->takes[t];
        FILE *f = fopen(tk->file, "rb");
        if (f) { fseek(f, 0, SEEK_END); file_bytes += (size_t)ftell(f); fclose(f); }
        if (tk->codec != SA_CODEC_AAC) {
            n_cached++;
            double a = now_s();
            int r = sa_cache_load_now(s, t);
            tload += now_s() - a;
            sa_entry *e = &s->entries[t];
            if (r != SA_OK) { bad++; printf("  LOAD FAIL %s\n", tk->file); continue; }
            if (tk->frames && e->frames != tk->frames) { frames_mismatch++; if (frames_mismatch < 5) printf("  frames %s: file %u manifest %u\n", tk->file, e->frames, tk->frames); }
            if (e->channels != tk->channels || e->rate != tk->rate) { bad++; printf("  format %s: %d ch %d Hz vs manifest %d/%d\n", tk->file, e->channels, e->rate, tk->channels, tk->rate); }
            if (e->fmt == SA_FMT_PCM16LE) bytes_pcm += e->bytes; else bytes_adpcm += e->bytes;
            /* release again: the playback pass loads through the cache like the game would */
            sa_free(s, e->data); e->data = NULL; SA_SUB(&s->cache_bytes, e->bytes); e->bytes = 0; e->state = SA_E_EMPTY;
            s->doneq_head = s->doneq_tail;
        } else {
            n_stream++;
            if (!aac) { aac_bad++; continue; }
            size_t len; char *buf = sa_read_file(tk->file, &len);
            if (!buf) { aac_bad++; printf("  AAC read fail %s\n", tk->file); continue; }
            double a = now_s();
            void *d = aac->open(tk->channels, tk->rate);
            long total = 0; size_t off = 0; int err = 0;
            while (d && off + 7 <= len) {
                sa_adts h;
                if (sa_adts_parse((const uint8_t *)buf + off, (int)(len - off), &h) != 1 || (size_t)h.frame_len > len - off) { err = 1; break; }
                if (h.channels != tk->channels || h.rate != tk->rate) err = 1;
                int n = aac->decode(d, (const uint8_t *)buf + off, h.frame_len, pcm, 4096);
                if (n < 0) { err = 1; break; }
                total += n; off += (size_t)h.frame_len;
            }
            if (d) aac->close(d);
            taac += now_s() - a;
            free(buf);
            long pad = total - tk->priming - (long)tk->frames;
            if (err || pad < 0 || pad > 2048) { aac_bad++; if (aac_bad < 5) printf("  AAC %s: err %d decoded %ld priming %d manifest %u (pad %ld)\n", tk->file, err, total, tk->priming, tk->frames, pad); }
            if (pad > max_pad) max_pad = (int)pad;
            if (pad < min_pad) min_pad = (int)pad;
            aac_seconds += (double)tk->frames / tk->rate;
        }
    }
    printf("takes: %d cached (%d failed, %d frame-count mismatches; RAM if all resident: pcm16 %.1f MiB + adpcm %.1f MiB; load %.0f ms total)\n",
           n_cached, bad, frames_mismatch, bytes_pcm / 1048576.0, bytes_adpcm / 1048576.0, tload * 1e3);
    printf("       %d aac-adts (%d bad; %.0f s of audio decoded in %.0f ms; encoder padding after frames: %d..%d)\n",
           n_stream, aac_bad, aac_seconds, taac * 1e3, min_pad == (1 << 30) ? 0 : min_pad, max_pad);
    printf("       files on disk: %.1f MiB\n", file_bytes / 1048576.0);
    /* ---- 2. play every event ---- */
    sa_wav w; int have_wav = wav && sa_wav_open(&w, wav, 48000, 0) == SA_OK;
    sa_set_poll_mask(s, SA_CB_STOPPED | SA_CB_DESTROYED);
    int ev_ok = 0, ev_late = 0, ev_held = 0, ev_never = 0, ev_silent = 0; double worst_late = 0, worst_early = 0;
    int n_sf = 0, n_tail = 0, n_timer = 0;
    long frames_total = 0; float peak = 0;
    float buf[2 * 256];
    double tmix0 = now_s();
    sa_set_timing(s, 1);
    for (int e0 = 0; e0 < s->nevents; e0 += batch) {
        sa_handle hs[256]; long start[256], expect[256], stopped[256]; int n = 0;
        for (int e = e0; e < s->nevents && e < e0 + batch; e++) {
            const sa_event *ev = &s->events[e];
            if (ev->is_snapshot) continue;
            sa_handle h = sa_event_create_index(s, e);
            if (!h) continue;
            sa_instance_start(s, h);
            hs[n] = h; start[n] = frames_total; stopped[n] = -1;
            long ex = -1;
            if (ev->oneshot && ev->ntakes) { ex = 0; for (int q = 0; q < ev->ntakes; q++) { const sa_take *tk = &s->takes[s->take_order[ev->first_take + q]]; long due = tk->stop_frame >= 0 ? tk->stop_frame : (long)tk->frames; if (due > ex) ex = due; } }
            else if (ev->oneshot && !ev->ntakes) { ex = ev->length_ms > 0 ? ev->length_ms * 48L : 0; ev_silent++; }
            expect[n] = ex;
            n++;
        }
        long cap = (long)cap_ms * 48;
        /* held events are stopped (with fade-out) at the cap: wait for the longest measured release in the batch + 1 s */
        long rel_wait = 0;
        for (int z = 0; z < n; z++) { const sa_event *ev = &s->events[sa_instance_get_event(s, hs[z])]; if ((long)ev->release_stop_frames > rel_wait) rel_wait = (long)ev->release_stop_frames; }
        for (long f = 0; f <= cap + 48000 + rel_wait; f += 256) {
            sa_update(s);
            sa_notification nn[64]; int k;
            while ((k = sa_poll(s, nn, 64)) > 0)
                for (int q = 0; q < k; q++) if (nn[q].type == SA_CB_STOPPED)
                    for (int z = 0; z < n; z++) if (hs[z] == nn[q].instance && stopped[z] < 0) stopped[z] = frames_total;
            if (f == cap) for (int z = 0; z < n; z++) if (stopped[z] < 0) { sa_instance_stop(s, hs[z], 1); if (expect[z] < 0 || expect[z] > cap) ev_held++; }
            int alive = 0; for (int z = 0; z < n; z++) if (stopped[z] < 0) alive = 1;
            if (!alive && f > 2048) break;
            sa_mix(s, buf, 256);
            for (int q = 0; q < 512; q++) { float a = fabsf(buf[q]); if (a > peak) peak = a; }
            if (have_wav) sa_wav_write(&w, buf, 256);
            frames_total += 256;
        }
        for (int z = 0; z < n; z++) {
            sa_instance_release(s, hs[z]);
            if (stopped[z] < 0) { ev_never++; printf("  never STOPPED: %s\n", s->events[sa_instance_get_event(s, hs[z]) >= 0 ? sa_instance_get_event(s, hs[z]) : 0].path); continue; }
            if (expect[z] >= 0 && expect[z] <= (long)cap_ms * 48) {
                sa_instance *ii = &s->inst[hs[z] & 0xFFF];
                int tk = ii->layers[0].take;
                const sa_event *iev = &s->events[ii->event];
                if (iev->ntakes && ii->in_silent) expect[z] = iev->silent_stop_frames >= 0 ? iev->silent_stop_frames : (iev->length_ms > 0 ? iev->length_ms * 48L : 0);
                else if (iev->ntakes && tk >= 0) {
                    const sa_take *t = &s->takes[tk];
                    expect[z] = t->stop_frame >= 0 ? t->stop_frame : (long)t->frames;
                    if (t->stop_frame >= 0) { n_sf++; if ((uint32_t)t->stop_frame < t->frames) n_tail++; else if ((uint32_t)t->stop_frame > t->frames) n_timer++; }
                }
                double late = (stopped[z] - start[z] - expect[z]) / 48.0;
                if ((late > 30 || late < -1) && ev_late < 12) printf("  timing %s: take %ld frames (%s), STOPPED after %ld (%+.1f ms)\n", s->events[ii->event].path, expect[z],
                    s->takes[tk].codec == SA_CODEC_AAC ? "aac" : s->takes[tk].codec == SA_CODEC_ADPCM ? "adpcm" : "pcm16", stopped[z] - start[z], late);
                if (late > worst_late) worst_late = late;
                if (late < worst_early) worst_early = late;
                if (late > 30 || late < -1) ev_late++; else ev_ok++;
            }
        }
        for (int q = 0; q < 8; q++) { sa_update(s); sa_mix(s, buf, 256); if (have_wav) sa_wav_write(&w, buf, 256); frames_total += 256; }
    }
    double tmix = now_s() - tmix0;
    if (have_wav) sa_wav_close(&w);
    sa_stats st; sa_get_stats(s, &st);
    printf("events: %d one-shots STOPPED on time (worst %.1f ms late, %.1f ms early vs FMOD's STOPPED / the take end), %d late/early, %d held stopped at the cap, %d never stopped, %d silent\n",
           ev_ok, worst_late, worst_early, ev_late, ev_held, ev_never, ev_silent);
    printf("        timed against the bake's stopFrame: %d (%d with a tail after STOPPED, %d with silence before it)\n", n_sf, n_tail, n_timer);
    printf("render: %.1f s of audio in %.2f s wall (%.0fx real time incl. sync AAC decode), peak %.3f (%.2f dBFS), steals %u, virtualized %u, underruns %u, load failures %u, cache peak budget %.0f MiB\n",
           frames_total / 48000.0, tmix, frames_total / 48000.0 / tmix, peak, 20 * log10(peak > 1e-9 ? peak : 1e-9), st.steals, st.virtualized, st.underruns, st.load_failures, st.cache_budget / 1048576.0);
    if (have_wav) printf("wav: %s\n", wav);
    free(pcm);
    sa_shutdown(s);
    return (bad || aac_bad || ev_never) ? 1 : 0;
}
