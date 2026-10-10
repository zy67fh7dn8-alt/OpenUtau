using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Golden {
    /// <summary>
    /// Exports intermediate data (golden data) for the Flutter port.
    /// Does nothing unless GOLDEN_OUT is set, so the normal test run is unaffected.
    ///
    ///   GOLDEN_UST=path/to/main.ust GOLDEN_SINGER=path/to/voicebank GOLDEN_OUT=path/to/golden/case \
    ///     dotnet test OpenUtau.Test --filter FullyQualifiedName~GoldenExportTest
    ///
    /// Outputs:
    ///   input.json    notes parsed from the UST (stage 0)
    ///   phonemes.json phonemizer output after timing validation (stage 1)
    ///   phrases.json  RenderPhrase.FromPart output (stage 2)
    /// </summary>
    public class GoldenExportTest {
        readonly ITestOutputHelper output;

        public GoldenExportTest(ITestOutputHelper output) {
            this.output = output;
        }

        static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        [Fact]
        public void Export() {
            var outDir = Environment.GetEnvironmentVariable("GOLDEN_OUT");
            if (string.IsNullOrEmpty(outDir)) {
                output.WriteLine("GOLDEN_OUT is not set. Skipped.");
                return;
            }
            var ustPath = Environment.GetEnvironmentVariable("GOLDEN_UST");
            var singerDir = Environment.GetEnvironmentVariable("GOLDEN_SINGER");
            Assert.True(File.Exists(ustPath), $"GOLDEN_UST not found: {ustPath}");
            Assert.True(Directory.Exists(singerDir), $"GOLDEN_SINGER not found: {singerDir}");
            Directory.CreateDirectory(outDir);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var singer = LoadSinger(singerDir);

            var project = Ust.Load(new[] { ustPath });
            var track = project.tracks[0];
            var part = project.parts.OfType<UVoicePart>().First();
            track.Singer = singer;

            Phonemize(project, part);
            var phrases = RenderPhrase.FromPart(project, track, part);

            Write(outDir, "input.json", ExportInput(project, part, ustPath, singerDir));
            Write(outDir, "phonemes.json", ExportPhonemes(part));
            Write(outDir, "phrases.json", phrases.Select(ph => ExportPhrase(ph, part.position)).ToList());
            output.WriteLine($"notes={part.notes.Count} phonemes={part.phonemes.Count} phrases={phrases.Count} -> {outDir}");
        }

        static ClassicSinger LoadSinger(string singerDir) {
            singerDir = Path.GetFullPath(singerDir);
            VoicebankLoader.IsTest = true;
            var voicebank = new Voicebank() {
                File = Path.Join(singerDir, "character.txt"),
                BasePath = Path.GetDirectoryName(singerDir),
            };
            VoicebankLoader.LoadVoicebank(voicebank);
            var singer = new ClassicSinger(voicebank);
            singer.EnsureLoaded();
            return singer;
        }

        /// <summary>
        /// Runs the real PhonemizerRunner outside the UI loop and waits until the part has applied its response.
        /// </summary>
        static void Phonemize(UProject project, UVoicePart part) {
            typeof(DocManager).GetProperty(nameof(DocManager.Project))!.SetValue(DocManager.Inst, project);
            using var runner = new PhonemizerRunner(TaskScheduler.Default);
            DocManager.Inst.SetPhonemizerRunnerForTest(runner);
            try {
                project.ValidateFull();
                var sw = Stopwatch.StartNew();
                while (!part.PhonemesUpToDate) {
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Phonemizer timed out.");
                    Thread.Sleep(10);
                }
            } finally {
                DocManager.Inst.SetPhonemizerRunnerForTest(null);
            }
            project.Validate(new ValidateOptions { SkipPhonemizer = true });
        }

        static void Write(string dir, string name, object data) {
            File.WriteAllText(Path.Join(dir, name), JsonSerializer.Serialize(data, jsonOptions) + "\n");
        }

        static object ExportInput(UProject project, UVoicePart part, string ustPath, string singerDir) => new {
            source = Path.GetFileName(ustPath),
            singer = Path.GetFileName(Path.GetFullPath(singerDir).TrimEnd(Path.DirectorySeparatorChar)),
            resolution = project.resolution,
            tempos = project.tempos.Select(t => new { t.position, t.bpm }),
            timeSignatures = project.timeSignatures.Select(t => new { t.barPosition, t.beatPerBar, t.beatUnit }),
            part = new { part.position, part.Duration },
            notes = part.notes.Select((n, i) => new {
                index = i,
                n.position,
                n.duration,
                n.tone,
                n.lyric,
                pitch = n.pitch.data.Select(p => new { p.X, p.Y, shape = p.shape.ToString() }),
                vibrato = new { n.vibrato.length, n.vibrato.period, n.vibrato.depth, n.vibrato.@in, n.vibrato.@out, n.vibrato.shift, n.vibrato.drift },
            }),
        };

        static object ExportPhonemes(UVoicePart part) {
            var notes = part.notes.ToList();
            return part.phonemes.Select(p => new {
                noteIndex = notes.IndexOf(p.Parent),
                p.index,
                p.rawPhoneme,
                p.phoneme,
                p.position,
                p.Duration,
                p.End,
                p.preutter,
                p.overlap,
                p.autoPreutter,
                p.autoOverlap,
                oto = ExportOto(p.oto),
                error = p.Error,
            }).ToList();
        }

        static object? ExportOto(UOto? oto) => oto == null ? null : new {
            oto.Alias,
            file = Path.GetFileName(oto.File),
            oto.Offset,
            oto.Consonant,
            oto.Cutoff,
            oto.Preutter,
            oto.Overlap,
        };

        static object ExportPhrase(RenderPhrase phrase, int partPosition) => new {
            phrase.position,
            phrase.duration,
            phrase.end,
            phrase.leading,
            phrase.positionMs,
            phrase.durationMs,
            phrase.endMs,
            phrase.leadingMs,
            // pitches etc. are sampled every 5 ticks from pitchStart (part-relative), as in RenderPhrase
            pitchIntervalTicks = 5,
            pitchStart = phrase.position - partPosition - phrase.leading,
            notes = phrase.notes.Select(n => new {
                n.lyric,
                n.tone,
                n.position,
                n.duration,
                n.positionMs,
                n.durationMs,
            }),
            phones = phrase.phones.Select(p => new {
                p.phoneme,
                p.tone,
                p.noteIndex,
                p.position,
                p.duration,
                p.leading,
                p.positionMs,
                p.durationMs,
                p.leadingMs,
                p.preutterMs,
                p.overlapMs,
                p.durCorrectionMs,
                p.tempo,
                p.adjustedTempo,
                p.volume,
                p.velocity,
                p.modulation,
                p.direct,
                p.toneShift,
                flags = p.flags.Select(f => new { flag = f.Item1, value = f.Item2, abbr = f.Item3 }),
                envelope = p.envelope.Select(v => new[] { v.X, v.Y }),
                oto = ExportOto(p.oto),
            }),
            phrase.pitches,
            phrase.pitchesBeforeDeviation,
            phrase.dynamics,
            phrase.gender,
            phrase.breathiness,
            phrase.toneShift,
            phrase.tension,
            phrase.voicing,
        };
    }
}
