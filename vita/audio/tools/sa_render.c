/* Scenario renderer: a JSON script of timed API calls -> WAV + JSON log (for the verifier). Offline and silent:
 * the mix goes to a file, no audio device is ever opened.
 *   sa_render --manifest M.json --script S.json --out OUT.wav [--float] [--rate 48000] [--block 256]
 * Script: { "durationMs":4000, "updateHz":60, "block":256, "seed":1, "float":false,
 *           "config":{"maxVoices":48,"maxStreams":8,"cacheBudgetMiB":24,"fadeoutMs":60,"paramXfadeMs":80,"snapshotFadeMs":200},
 *           "calls":[ {"t":0,"op":"play","event":"event:/x","id":"a"}, ... ] }
 * ops: create start play(create+start+release) stop(fade) release set_param set_volume set_pitch set_paused set_3d(pos,vel)
 *      listener(pos,vel,forward,up) bus_volume bus_mute bus_paused bus_stop_all(fade) snapshot_start snapshot_stop(fade)
 *      global_param master expect_state(state) mark(label)
 * Log (<out>.log.json): notifications with frame/ms, expect results, final stats. Calls apply at the first game frame
 * whose time >= t (game frames are 1000/updateHz ms apart; sa_update runs once per game frame). */
#include "sa_internal.h"

typedef struct idmap { char id[64]; sa_handle h; } idmap;
static idmap g_ids[1024]; static int g_nids;
static sa_handle hid(const char *id) { for (int k = 0; k < g_nids; k++) if (!strcmp(g_ids[k].id, id)) return g_ids[k].h; return 0; }
static const char *idof(sa_handle h) { for (int k = g_nids - 1; k >= 0; k--) if (g_ids[k].h == h) return g_ids[k].id; return "?"; }
static void setid(const char *id, sa_handle h)
{
    if (!id) return;
    for (int k = 0; k < g_nids; k++) if (!strcmp(g_ids[k].id, id)) { g_ids[k].h = h; return; }
    if (g_nids < 1024) { snprintf(g_ids[g_nids].id, sizeof g_ids[0].id, "%s", id); g_ids[g_nids++].h = h; }
}
static int vec(const sa_json *a, sa_vec3 *v)
{
    if (!a || a->type != SA_J_ARR || a->count < 3) return 0;
    const sa_json *c = a->child; v->x = (float)c->num; c = c->next; v->y = (float)c->num; c = c->next; v->z = (float)c->num;
    return 1;
}
static const char *tname(uint32_t t)
{
    switch (t) {
    case SA_CB_STARTING: return "STARTING"; case SA_CB_STARTED: return "STARTED"; case SA_CB_RESTARTED: return "RESTARTED";
    case SA_CB_STOPPED: return "STOPPED"; case SA_CB_DESTROYED: return "DESTROYED"; case SA_CB_REAL_TO_VIRTUAL: return "REAL_TO_VIRTUAL";
    case SA_CB_VIRTUAL_TO_REAL: return "VIRTUAL_TO_REAL"; default: return "OTHER";
    }
}
static const char *sname(int s)
{
    switch (s) { case 0: return "PLAYING"; case 1: return "SUSTAINING"; case 2: return "STOPPED"; case 3: return "STARTING"; case 4: return "STOPPING"; default: return "INVALID"; }
}

int main(int argc, char **argv)
{
    const char *mpath = NULL, *spath = NULL, *out = NULL; int flt = -1, rate = 48000, block = -1;
    for (int k = 1; k < argc; k++) {
        if (!strcmp(argv[k], "--manifest") && k + 1 < argc) mpath = argv[++k];
        else if (!strcmp(argv[k], "--script") && k + 1 < argc) spath = argv[++k];
        else if (!strcmp(argv[k], "--out") && k + 1 < argc) out = argv[++k];
        else if (!strcmp(argv[k], "--float")) flt = 1;
        else if (!strcmp(argv[k], "--rate") && k + 1 < argc) rate = atoi(argv[++k]);
        else if (!strcmp(argv[k], "--block") && k + 1 < argc) block = atoi(argv[++k]);
        else { fprintf(stderr, "unknown arg %s\n", argv[k]); return 2; }
    }
    if (!spath || !out) { fprintf(stderr, "usage: sa_render --manifest M --script S --out OUT.wav [--float] [--rate R] [--block N]\n"); return 2; }
    size_t len; char *txt = sa_read_file(spath, &len);
    if (!txt) { fprintf(stderr, "cannot read %s\n", spath); return 2; }
    sa_jarena ar = { 0 }; char err[256];
    sa_json *sc = sa_json_parse(txt, len, &ar, err, sizeof err);
    if (!sc) { fprintf(stderr, "script: %s\n", err); return 2; }
    if (!mpath) mpath = sa_json_str(sa_json_get(sc, "manifest"), NULL);
    if (!mpath) { fprintf(stderr, "no manifest\n"); return 2; }
    sa_config c; sa_config_defaults(&c);
    c.output_rate = rate; c.flags = SA_FLAG_SYNC_IO; c.seed = (uint32_t)sa_json_num(sa_json_get(sc, "seed"), 1);
    const sa_json *cj = sa_json_get(sc, "config");
    if (cj) {
        c.max_voices = (int)sa_json_num(sa_json_get(cj, "maxVoices"), c.max_voices);
        c.max_streams = (int)sa_json_num(sa_json_get(cj, "maxStreams"), c.max_streams);
        c.cache_budget_bytes = (size_t)(sa_json_num(sa_json_get(cj, "cacheBudgetMiB"), 24) * 1048576.0);
        c.fadeout_ms = (float)sa_json_num(sa_json_get(cj, "fadeoutMs"), c.fadeout_ms);
        c.param_xfade_ms = (float)sa_json_num(sa_json_get(cj, "paramXfadeMs"), c.param_xfade_ms);
        c.snapshot_fade_ms = (float)sa_json_num(sa_json_get(cj, "snapshotFadeMs"), c.snapshot_fade_ms);
        c.doppler_scale = (float)sa_json_num(sa_json_get(cj, "dopplerScale"), 0);
    }
    sa_system *s = sa_init_ex(mpath, &c);
    if (!s) { fprintf(stderr, "init: %s\n", sa_last_error()); return 1; }
    sa_set_poll_mask(s, SA_CB_ALL);
    double dur = sa_json_num(sa_json_get(sc, "durationMs"), 5000);
    double hz = sa_json_num(sa_json_get(sc, "updateHz"), 60);
    if (block <= 0) block = (int)sa_json_num(sa_json_get(sc, "block"), 256);
    if (flt < 0) flt = sa_json_bool(sa_json_get(sc, "float"), 0);
    const sa_json *calls = sa_json_get(sc, "calls");
    char logp[1200]; snprintf(logp, sizeof logp, "%s.log.json", out);
    FILE *lg = fopen(logp, "w");
    sa_wav w;
    if (!lg || sa_wav_open(&w, out, rate, flt)) { fprintf(stderr, "cannot write %s\n", out); return 1; }
    fprintf(lg, "{\"manifest\":\"%s\",\"rate\":%d,\"block\":%d,\"updateHz\":%g,\n\"log\":[\n", mpath, rate, block, hz);
    int first_log = 1, nexp = 0, nfail = 0, nerr = 0;
#define LOGL(...) do { fprintf(lg, "%s", first_log ? " " : ",\n "); first_log = 0; fprintf(lg, __VA_ARGS__); } while (0)
    long total = (long)(dur * rate / 1000.0);
    double period = rate / hz;
    float *buf = (float *)malloc(sizeof(float) * 2 * (size_t)block);
    const sa_json *next = calls && calls->type == SA_J_ARR ? calls->child : NULL;
    long frame = 0; long g = 0;
    while (frame < total) {
        double tms = frame * 1000.0 / rate;
        for (; next && sa_json_num(sa_json_get(next, "t"), 0) <= tms + 1e-9; next = next->next) {
            const char *op = sa_json_str(sa_json_get(next, "op"), "");
            const char *id = sa_json_str(sa_json_get(next, "id"), NULL);
            const char *ev = sa_json_str(sa_json_get(next, "event"), sa_json_str(sa_json_get(next, "path"), NULL));
            const char *bus = sa_json_str(sa_json_get(next, "bus"), NULL);
            const sa_json *val = sa_json_get(next, "value");
            float v = (float)sa_json_num(val, 0);
            int fade = sa_json_bool(sa_json_get(next, "fade"), 1);
            sa_handle h = id ? hid(id) : 0;
            int r = SA_OK;
            if (!strcmp(op, "create") || !strcmp(op, "play")) {
                h = sa_event_create(s, ev); setid(id, h);
                if (!h) r = SA_ERR_NOT_FOUND;
                else if (!strcmp(op, "play")) { sa_instance_start(s, h); sa_instance_release(s, h); }
            }
            else if (!strcmp(op, "start")) r = sa_instance_start(s, h);
            else if (!strcmp(op, "stop")) r = sa_instance_stop(s, h, fade);
            else if (!strcmp(op, "release")) r = sa_instance_release(s, h);
            else if (!strcmp(op, "set_param")) r = sa_instance_set_param(s, h, sa_json_str(sa_json_get(next, "name"), ""), v);
            else if (!strcmp(op, "set_volume")) r = sa_instance_set_volume(s, h, v);
            else if (!strcmp(op, "set_pitch")) r = sa_instance_set_pitch(s, h, v);
            else if (!strcmp(op, "set_paused")) r = sa_instance_set_paused(s, h, sa_json_bool(val, 1));
            else if (!strcmp(op, "set_3d")) { sa_vec3 p = { 0, 0, 0 }, ve = { 0, 0, 0 }; vec(sa_json_get(next, "pos"), &p); vec(sa_json_get(next, "vel"), &ve); r = sa_instance_set_3d(s, h, &p, &ve); }
            else if (!strcmp(op, "listener")) {
                sa_vec3 p = { 0, 0, 0 }, ve = { 0, 0, 0 }, f = { 0, 0, 1 }, u = { 0, 1, 0 };
                vec(sa_json_get(next, "pos"), &p); vec(sa_json_get(next, "vel"), &ve); vec(sa_json_get(next, "forward"), &f); vec(sa_json_get(next, "up"), &u);
                r = sa_set_listener(s, &p, &ve, &f, &u);
            }
            else if (!strcmp(op, "bus_volume")) r = sa_bus_set_volume(s, bus, v);
            else if (!strcmp(op, "bus_mute")) r = sa_bus_set_mute(s, bus, sa_json_bool(val, 1));
            else if (!strcmp(op, "bus_paused")) r = sa_bus_set_paused(s, bus, sa_json_bool(val, 1));
            else if (!strcmp(op, "bus_stop_all")) r = sa_bus_stop_all(s, bus, fade);
            else if (!strcmp(op, "snapshot_start")) { h = sa_snapshot_start(s, ev); setid(id, h); if (!h) r = SA_ERR_NOT_FOUND; }
            else if (!strcmp(op, "snapshot_stop")) r = sa_snapshot_stop(s, h, fade);
            else if (!strcmp(op, "global_param")) r = sa_set_global_param(s, sa_json_str(sa_json_get(next, "name"), ""), v);
            else if (!strcmp(op, "master")) r = sa_set_master_volume(s, v);
            else if (!strcmp(op, "expect_state")) {
                const char *want = sa_json_str(sa_json_get(next, "state"), "");
                const char *got = sname(sa_instance_get_playback_state(s, h));
                int ok = !strcmp(want, got);
                nexp++; if (!ok) nfail++;
                LOGL("{\"kind\":\"expect\",\"frame\":%ld,\"ms\":%.3f,\"id\":\"%s\",\"want\":\"%s\",\"got\":\"%s\",\"ok\":%s}", frame, tms, id ? id : "", want, got, ok ? "true" : "false");
                continue;
            }
            else if (!strcmp(op, "mark")) { LOGL("{\"kind\":\"mark\",\"frame\":%ld,\"ms\":%.3f,\"label\":\"%s\"}", frame, tms, sa_json_str(sa_json_get(next, "label"), "")); continue; }
            else r = SA_ERR_UNSUPPORTED;
            if (r != SA_OK) { nerr++; LOGL("{\"kind\":\"error\",\"frame\":%ld,\"ms\":%.3f,\"op\":\"%s\",\"id\":\"%s\",\"code\":%d}", frame, tms, op, id ? id : "", r); }
        }
        sa_update(s);
        sa_notification nn[64]; int k;
        while ((k = sa_poll(s, nn, 64)) > 0)
            for (int q = 0; q < k; q++)
                LOGL("{\"kind\":\"notify\",\"frame\":%ld,\"ms\":%.3f,\"id\":\"%s\",\"event\":%d,\"type\":\"%s\"}", frame, tms, idof(nn[q].instance), nn[q].event, tname(nn[q].type));
        g++;
        long until = (long)(g * period + 0.5); if (until > total) until = total;
        while (frame < until) {
            int n = (int)(until - frame); if (n > block) n = block;
            sa_mix(s, buf, n);
            sa_wav_write(&w, buf, n);
            frame += n;
        }
    }
    sa_wav_close(&w);
    sa_stats st; sa_get_stats(s, &st);
    fprintf(lg, "\n],\n\"frames\":%ld,\"expects\":%d,\"expectFailures\":%d,\"errors\":%d,\n", frame, nexp, nfail, nerr);
    fprintf(lg, "\"stats\":{\"voicesMax\":%d,\"steals\":%u,\"virtualized\":%u,\"devirtualized\":%u,\"hardCuts\":%u,\"underruns\":%u,\"cacheBytes\":%llu,"
                "\"cacheHits\":%u,\"cacheMisses\":%u,\"evictions\":%u,\"loadFailures\":%u,\"cmdPeak\":%u,\"cmdDropped\":%u,\"notifyDropped\":%u,\"liveAtEnd\":%d}\n}\n",
            st.voices_max, st.steals, st.virtualized, st.devirtualized, st.hard_cuts, st.underruns, (unsigned long long)st.cache_bytes, st.cache_hits,
            st.cache_misses, st.cache_evictions, st.load_failures, st.cmd_queue_peak, st.cmd_dropped, st.notify_dropped, st.instances_live);
    fclose(lg);
    printf("rendered %ld frames to %s (log %s): %d expects, %d failed, %d call errors\n", frame, out, logp, nexp, nfail, nerr);
    free(buf); sa_shutdown(s); sa_json_free(&ar); free(txt);
    return nfail || nerr ? 1 : 0;
}
