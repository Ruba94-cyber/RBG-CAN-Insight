using System.Text.Json;
using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class StudioSavedAnalysisTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rbg-studio-" + Guid.NewGuid().ToString("N"));
    private readonly StudioSavedAnalysisStore _store;

    public StudioSavedAnalysisTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StudioSavedAnalysisStore(Path.Combine(_dir, "analyses"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static StudioSavedAnalysis Make(string name, IReadOnlyList<StudioSourceRef>? sources = null,
        IReadOnlyList<TraceNotebookPage>? pages = null) =>
        new(1, Guid.NewGuid().ToString("N"), name, DateTime.UtcNow, sources ?? [], "ByTime", "id=0x123", "rule", 2, 2,
            pages ?? [], null);

    [Fact]
    public void SaveEList_RoundTripConservaFiltriTabESezione()
    {
        var page = new TraceNotebookPage("p1", "Engine speed", "id=0x18F", 1, 0, null, "note");
        var saved = _store.Save(Make("Run 1", pages: [page]) with { ActiveTabId = "p1" });

        var list = _store.List(out var problems);

        Assert.Empty(problems);
        var loaded = Assert.Single(list);
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("id=0x123", loaded.TraceSearchText);
        Assert.Equal("rule", loaded.TraceRuleText);
        Assert.Equal(2, loaded.SectionIndex);
        Assert.Equal("p1", loaded.ActiveTabId);
        Assert.Equal("Engine speed", Assert.Single(loaded.Pages).Title);
        Assert.Equal("id=0x18F", loaded.Pages[0].Query);
    }

    [Fact]
    public void NomeDuplicato_VieneRifiutatoSalvoSostituzioneEsplicita_EnonCreaUnSecondoFile()
    {
        _store.Save(Make("Run"));
        var ex = Assert.Throws<StudioSavedAnalysisException>(() => _store.Save(Make("  RUN ")));
        Assert.Contains("already exists", ex.Message);

        var replaced = _store.Save(Make("run") with { TraceSearchText = "nuovo" }, replaceSameName: true);

        var unico = Assert.Single(_store.List(out _));
        Assert.Equal("nuovo", unico.TraceSearchText);
        Assert.Equal(replaced.Id, unico.Id);
    }

    [Fact]
    public void NomeVuotoOTroppoLungo_Normalizzato()
    {
        Assert.Throws<StudioSavedAnalysisException>(() => _store.Save(Make("   ")));
        var saved = _store.Save(Make(new string('x', 500)));
        Assert.Equal(StudioSavedAnalysisStore.MaxNameChars, saved.Name.Length);
    }

    [Fact]
    public void ScritturaAtomica_NessunTemporaneoResiduo()
    {
        for (var i = 0; i < 5; i++) _store.Save(Make("A" + i));
        var cartella = Path.Combine(_dir, "analyses");
        Assert.Empty(Directory.GetFiles(cartella, "*.tmp*"));
        Assert.Equal(5, Directory.GetFiles(cartella, "*.rbganalysis.json").Length);
    }

    [Fact]
    public void VersioneFutura_ERifiutataSegnalataENonSovrascritta()
    {
        var saved = _store.Save(Make("Vecchia"));
        var file = Path.Combine(_dir, "analyses", saved.Id + ".rbganalysis.json");
        var futuro = File.ReadAllText(file).Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99");
        File.WriteAllText(file, futuro);

        var list = _store.List(out var problems);

        Assert.Empty(list);
        Assert.Contains(problems, p => p.Contains("newer version"));
        Assert.Throws<StudioSavedAnalysisException>(() => _store.Load(saved.Id));
        Assert.Equal(futuro, File.ReadAllText(file));
    }

    [Fact]
    public void FileCorrotto_SegnalatoMaNonCancellato()
    {
        _store.Save(Make("Ok"));
        var rotto = Path.Combine(_dir, "analyses", Guid.NewGuid().ToString("N") + ".rbganalysis.json");
        File.WriteAllText(rotto, "{ non json");

        var list = _store.List(out var problems);

        Assert.Single(list);
        Assert.Single(problems);
        Assert.True(File.Exists(rotto));
    }

    [Fact]
    public void RinominaEEliminaFunzionanoEImpedisconoNomiDuplicati()
    {
        var a = _store.Save(Make("A"));
        var b = _store.Save(Make("B"));

        Assert.Throws<StudioSavedAnalysisException>(() => _store.Rename(b.Id, "a"));
        Assert.Equal("C", _store.Rename(b.Id, "C").Name);
        Assert.True(_store.Delete(a.Id));
        Assert.False(_store.Delete(a.Id));
        Assert.Equal("C", Assert.Single(_store.List(out _)).Name);
    }

    [Fact]
    public void IdNonValido_NonPuoUscireDallaCartella()
    {
        Assert.Throws<StudioSavedAnalysisException>(() => _store.Load("..\\..\\windows\\system32\\x"));
        Assert.Throws<StudioSavedAnalysisException>(() => _store.Delete("../evil"));
    }

    [Fact]
    public void SorgentiInvariate_SonoOk()
    {
        var path = WriteSource("a.asc", "0.0 1 123 Rx d 1 00");
        var saved = Make("S", [StudioSavedAnalysisStore.DescribeSource("trace", path)]);

        var checks = StudioSavedAnalysisStore.CheckSources(saved);

        Assert.Equal(StudioSourceState.Ok, Assert.Single(checks).State);
        Assert.Equal("", StudioSavedAnalysisStore.DescribeProblems(checks));
    }

    [Fact]
    public void SorgenteMancante_EModificata_VengonoRilevateEDescritte()
    {
        var manca = WriteSource("manca.asc", "x");
        var cambia = WriteSource("cambia.asc", "contenuto A");
        var uguale = WriteSource("uguale.asc", "invariato");
        var saved = Make("S", [
            StudioSavedAnalysisStore.DescribeSource("trace", manca),
            StudioSavedAnalysisStore.DescribeSource("trace", cambia),
            StudioSavedAnalysisStore.DescribeSource("dbc", uguale)]);

        File.Delete(manca);
        File.WriteAllText(cambia, "contenuto B"); // stessa lunghezza, hash diverso

        var checks = StudioSavedAnalysisStore.CheckSources(saved);

        Assert.Equal(StudioSourceState.Missing, checks[0].State);
        Assert.Equal(StudioSourceState.Changed, checks[1].State);
        Assert.Equal(StudioSourceState.Ok, checks[2].State);
        var testo = StudioSavedAnalysisStore.DescribeProblems(checks);
        Assert.Contains("Missing: manca.asc", testo);
        Assert.Contains("Changed since saved", testo);
        Assert.Contains("cambia.asc", testo);
        Assert.DoesNotContain("uguale.asc", testo);
    }

    [Fact]
    public void SorgenteConLunghezzaDiversa_EModificataSenzaCalcolareTuttoLHash()
    {
        var path = WriteSource("l.asc", "abc");
        var saved = Make("S", [StudioSavedAnalysisStore.DescribeSource("trace", path)]);
        File.WriteAllText(path, "abcdef");

        Assert.Equal(StudioSourceState.Changed, Assert.Single(StudioSavedAnalysisStore.CheckSources(saved)).State);
    }

    [Fact]
    public void DescribeSource_Sha256EAnnullabile()
    {
        var path = WriteSource("h.asc", "abc");
        var sorgente = StudioSavedAnalysisStore.DescribeSource("trace", path);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sorgente.Sha256);
        Assert.Equal(3, sorgente.Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudioSavedAnalysisStore.DescribeSource("trace", path, cts.Token));
    }

    [Fact]
    public void NonSiSalvanoDatiDelTracciatoNelFile()
    {
        var path = WriteSource("segreto.asc", "DATI-RISERVATI-123");
        _store.Save(Make("S", [StudioSavedAnalysisStore.DescribeSource("trace", path)]));

        var json = File.ReadAllText(Directory.GetFiles(Path.Combine(_dir, "analyses"), "*.rbganalysis.json").Single());

        Assert.DoesNotContain("DATI-RISERVATI-123", json);
        Assert.NotNull(JsonDocument.Parse(json));
    }
}
