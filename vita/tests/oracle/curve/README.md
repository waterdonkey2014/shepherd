# AnimationCurve.Evaluate against the Unity oracle (kit #9 fidelity, vita/notes/kit9-fidelity.md "curve")

The oracle's `d-data-curve` suite prints only the evaluation times and results; its inputs (keys, tangents, weights, wrap modes) come
from OracleCore's SplitMix64 generator. These scripts regenerate them exactly -- under Unity's player Mono rule (double intermediates)
AND the float32 rule (CoreCLR / Shepherd), because the probe's own `t += 0.05f + r.Unit() * 2f` rounds differently -- and test models
of Unity's native evaluation against the recorded player outputs. Mac only; the outputs they read are game-free (seeded probe data).

- `model.py` -- the generator (Rng, Seed, D_Curve's key generation, both float rules).
- `eval.py <dir> <u-x64 d-data-curve.tsv> <u-arm64 ...> [form pp-form rep-form]` -- the Unity model (`eval_u`: wrap rules, edge rules,
  the segment-cache cubic `cache` / per-key Hermite `hermite`) and kit #8's (`eval_shep`), tallied against both players.
- `big.py` -- the same, by region (inside / edge / pre / post) x wrap mode x ulp bucket.
- `k8tot.py` -- kit #8 vs kit #9 totals (exact / <= 16 ulp / 17-4096 / > 4096).
- `port.py <dir> <Shepherd d-data-curve.tsv> x cache hermite hermite` -- the engine's C# port (Icalls/DataIcalls.cs EvaluateUnity) against the
  model on the float32 inputs (the TestHost probe run): must be identical.

Usage: `python3 eval.py vita/tests/oracle/curve <oracle>/u-x64-s2/d-data-curve.tsv <oracle>/u-arm64-s2/d-data-curve.tsv cache hermite hermite`.
`CURVE_SEED=<s>` (default 2) regenerates another oracle seed's inputs: the second review's held-out check ran seeds 3 and 4 (vita/tests/oracle/run.sh
unity <arch> <out> d-data-curve <s> 300; the Shepherd side run.sh shepherd for port.py), the model unchanged -- vita/notes/kit9-fidelity.md 13.1.
