// Kit #7 tests track (vita/notes/kit7-tests.md): the Unity 2022.3.62f2 oracle player's driver -- the E1 pattern (a built macOS Mono player,
// batchmode, one fresh process per batch). Copied into the owner-local oracle project by vita/tests/oracle/setup.sh with the shared probe
// sources (vita/tests/probes). Arguments:
//   -oracle <suite[,suite...]|all> -seed <S> -n <N> -out <dir> [-first <F>]   the probe suites: <dir>/<suite>.tsv (the format the Mac test
//                                                                           host writes for Shepherd), then quits
//   -oscene <name[,name...]|all> -out <dir>                                 the one-feature render scenes (Scenes.cs): <dir>/<scene>.*
//   -ilsuite <dir> -out <dir>                                               the IL conformance suite (vita/tests/il): <dir>/conformance.exe's
//                                                                           driver loaded and run in THIS runtime (the game's own Mono)
// Silent: the project's audio is disabled (AudioManager m_DisableAudio 1) and every run is -batchmode (render scenes without -nographics).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class OracleMain : MonoBehaviour
{
    string _suites, _out = ".", _scenes, _il; ulong _seed = 1; int _n = 100, _first;

    void Awake()
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
        {
            switch (a[i])
            {
                case "-oracle": _suites = a[i + 1]; break;
                case "-oscene": _scenes = a[i + 1]; break;
                case "-seed": _seed = ulong.Parse(a[i + 1], CultureInfo.InvariantCulture); break;
                case "-n": _n = int.Parse(a[i + 1], CultureInfo.InvariantCulture); break;
                case "-first": _first = int.Parse(a[i + 1], CultureInfo.InvariantCulture); break;
                case "-out": _out = a[i + 1]; break;
                case "-ilsuite": _il = a[i + 1]; break;
            }
        }
    }

    IEnumerator Start()
    {
        Directory.CreateDirectory(_out);
        var log = new StringBuilder();
        log.Append("unity ").Append(Application.unityVersion).Append(" arch ").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
           .Append(" culture ").Append(CultureInfo.CurrentCulture.Name).Append(" editor ").Append(Application.isEditor).Append('\n');
        if (!string.IsNullOrEmpty(_suites))
        {
            var names = _suites == "all" ? new List<string>(ShepherdOracle.Suites.Names()) : new List<string>(_suites.Split(','));
            foreach (var s in names)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var w = new StreamWriter(Path.Combine(_out, s + ".tsv"), false, new UTF8Encoding(false)) { NewLine = "\n" })
                {
                    ulong h = ShepherdOracle.Suites.Run(s, _seed, _first, _n, w, m => log.Append(m).Append('\n'));
                    log.Append(s).Append('\t').Append(h.ToString("x16")).Append('\t').Append(sw.ElapsedMilliseconds).Append(" ms\n");
                }
                yield return null;   // (a frame between suites: DestroyImmediate'd objects fully gone)
            }
        }
        if (!string.IsNullOrEmpty(_il))
        {
            try
            {
                var drv = System.Reflection.Assembly.LoadFrom(Path.Combine(_il, "conformance.exe")).GetType("ShepherdConformance");
                var sb = new StringBuilder();
                var n = drv.GetMethod("Run").Invoke(null, new object[] { _il, sb });
                File.WriteAllText(Path.Combine(_out, "il.tsv"), sb.ToString());
                log.Append("ilsuite: ").Append(n).Append(" tests\n");
            }
            catch (Exception e) { log.Append("ilsuite: ").Append(e).Append('\n'); }
        }
        if (!string.IsNullOrEmpty(_scenes))
        {
            var e = ShepherdOracle.Scenes.RunUnity(_scenes, _out, log);
            while (e.MoveNext()) yield return e.Current;
        }
        File.WriteAllText(Path.Combine(_out, "oracle-run.txt"), log.ToString());
        Application.Quit(0);
    }
}
