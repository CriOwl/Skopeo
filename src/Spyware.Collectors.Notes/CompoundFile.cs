using System.Text;

namespace Spyware.Collectors.Notes;

/// <summary>Nombre y tamaño de una secuencia dentro de un archivo CFB.</summary>
public sealed record CompoundStream(string Name, long Size);

/// <summary>
/// Lector mínimo del formato MS-CFB (compound file binary, el "OLE" de siempre) que
/// necesita el `StickyNotes.snt` clásico: cabecera, FAT, cadenas de sectores, entrada
/// de directorio, mini FAT y mini secuencia. Nada más: sin STGPROPY, sin transacciones
/// y sin escrituras, porque el agente solo lee.
/// </summary>
public sealed class CompoundFile : IDisposable
{
    private const ulong Signature = 0xE11AB1A1E011CFD0UL;   // D0 CF 11 E0 A1 B1 1A E1
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint NoStream = 0xFFFFFFFF;
    private const int EntrySize = 128;
    private const int DifatEntries = 109;
    private const long MaxChainSectors = 1_000_000;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly long _miniCutOff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[]? _miniStream;
    private readonly List<Entry> _entries;

    private CompoundFile(byte[] data, int sectorSize, int miniSectorSize, long miniCutOff,
                         uint[] fat, uint[] miniFat, byte[]? miniStream, List<Entry> entries)
    {
        _data = data;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniCutOff = miniCutOff;
        _fat = fat;
        _miniFat = miniFat;
        _miniStream = miniStream;
        _entries = entries;
    }

    /// <summary>Comprueba la firma sin cargar el archivo (para no llamar "corrupto" a lo que no es CFB).</summary>
    public static bool HasSignature(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) != 8) return false;

            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header) == Signature;
        }
        catch
        {
            return false;
        }
    }

    public static CompoundFile Open(string path, long maxBytes = 64L * 1024 * 1024)
    {
        long length = new FileInfo(path).Length;
        if (length > maxBytes)
            throw new InvalidDataException($"archivo CFB demasiado grande ({length} bytes)");

        byte[] data = File.ReadAllBytes(path);
        Parse(data, out int sectorSize, out int miniSectorSize, out long miniCutOff,
              out uint[] fat, out uint[] miniFat, out byte[]? miniStream, out List<Entry> entries);

        return new CompoundFile(data, sectorSize, miniSectorSize, miniCutOff, fat, miniFat, miniStream, entries);
    }

    public IEnumerable<CompoundStream> EnumerateStreams() =>
        _entries.Where(entry => entry.Type == 2)
            .Select(entry => new CompoundStream(entry.Name, entry.Size))
            .ToList();

    /// <summary>Contenido de una secuencia por nombre (sin distinción de mayúsculas).</summary>
    public byte[]? ReadStream(string name, int maxBytes = 1024 * 1024)
    {
        Entry? entry = _entries.FirstOrDefault(e =>
            e.Type == 2 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (entry is null || entry.Size <= 0) return null;

        long size = Math.Min(entry.Size, maxBytes);

        return entry.Size < _miniCutOff
            ? ReadMiniStream(entry.StartSector, (int)size)
            : ReadFatChain(entry.StartSector, size);
    }

    public void Dispose()
    {
        _data.AsSpan().Clear();
        if (_miniStream is not null) _miniStream.AsSpan().Clear();
    }

    // ---------- análisis de la cabecera ----------

    private static void Parse(byte[] data, out int sectorSize, out int miniSectorSize, out long miniCutOff,
                              out uint[] fat, out uint[] miniFat, out byte[]? miniStream, out List<Entry> entries)
    {
        if (data.Length < 512 || ReadUInt64(data, 0) != Signature)
            throw new InvalidDataException("firma MS-CFB no válida");

        int majorVersion = ReadUInt16(data, 26);
        if (majorVersion is not (3 or 4))
            throw new InvalidDataException($"versión CFB no soportada: {majorVersion}");

        int sectorShift = ReadUInt16(data, 30);
        int miniSectorShift = ReadUInt16(data, 32);

        if (sectorShift is < 7 or > 16 || miniSectorShift is < 4 or > 16)
            throw new InvalidDataException("tamaño de sector fuera de rango");

        sectorSize = 1 << sectorShift;
        miniSectorSize = 1 << miniSectorShift;
        miniCutOff = ReadUInt32(data, 56);

        uint fatSectors = ReadUInt32(data, 44);
        uint firstDirectory = ReadUInt32(data, 48);
        uint firstMiniFat = ReadUInt32(data, 60);
        uint miniFatCount = ReadUInt32(data, 64);
        uint firstDifat = ReadUInt32(data, 68);
        uint difatCount = ReadUInt32(data, 72);

        List<uint> fatPositions = ReadDifat(data, firstDifat, difatCount, fatSectors, sectorSize);
        fat = ReadFat(data, fatPositions, sectorSize);

        List<Entry> directory = ReadDirectory(data, firstDirectory, fat, sectorSize);
        entries = directory;

        miniFat = ReadMiniFat(data, firstMiniFat, miniFatCount, fat, sectorSize, miniSectorSize);

        Entry? root = directory.FirstOrDefault(e => e.Type == 5);
        if (root is not null && root.Size > 0)
            miniStream = ReadFatChain(data, root.StartSector, Math.Min(root.Size, data.Length), sectorSize, fat);
        else
            miniStream = null;
    }

    private static List<uint> ReadDifat(byte[] data, uint firstDifat, uint difatCount, uint fatSectors, int sectorSize)
    {
        // Algunas escritoras dejan declarado 0 en "número de sectores FAT": se recorre
        // el DIFAT completo en lugar de cortar por una cifra que puede ser falsa.
        uint declared = fatSectors == 0 ? uint.MaxValue : fatSectors;

        var positions = new List<uint>();

        for (int index = 0; index < DifatEntries && positions.Count < declared; index++)
        {
            uint sector = ReadUInt32(data, 76 + index * 4);
            if (sector is FreeSector or NoStream) break;

            positions.Add(sector);
        }

        uint current = firstDifat;
        int guard = 0;

        while (current is not FreeSector and not NoStream && positions.Count < declared)
        {
            if (++guard > MaxChainSectors || !SectorRange(sectorSize, current, data.Length))
                throw new InvalidDataException("cadena DIFAT corrupta");

            int offset = SectorOffset(sectorSize, current);

            // MS-CFB: los identificadores de sector FAT ocupan todo el sector menos los
            // últimos 8 bytes, que guardan el siguiente sector DIFAT y el recuento.
            int entriesPerSector = sectorSize / 4 - 2;
            uint next = ReadUInt32(data, offset + sectorSize - 8);

            for (int index = 0; index < entriesPerSector && positions.Count < declared; index++)
            {
                uint sector = ReadUInt32(data, offset + index * 4);
                if (sector is FreeSector or NoStream) continue;

                positions.Add(sector);
            }

            current = next;
        }

        return positions;
    }

    private static uint[] ReadFat(byte[] data, List<uint> fatPositions, int sectorSize)
    {
        var fat = new List<uint>(fatPositions.Count * (sectorSize / 4));

        foreach (uint position in fatPositions)
        {
            if (!SectorRange(sectorSize, position, data.Length))
                throw new InvalidDataException("sector FAT fuera del archivo");

            int offset = SectorOffset(sectorSize, position);

            for (int index = 0; index < sectorSize / 4 && offset + index * 4 + 4 <= data.Length; index++)
                fat.Add(ReadUInt32(data, offset + index * 4));
        }

        return fat.ToArray();
    }

    private static uint[] ReadMiniFat(byte[] data, uint firstMiniFat, uint miniFatCount,
                                      uint[] fat, int sectorSize, int miniSectorSize)
    {
        if (firstMiniFat is FreeSector or NoStream || miniFatCount == 0)
            return [];

        var values = new List<uint>();
        uint? sector = firstMiniFat;
        int guard = 0;

        while (sector is not null && sector is not FreeSector and not NoStream)
        {
            if (++guard > MaxChainSectors || !SectorRange(sectorSize, sector.Value, data.Length))
                break;

            int offset = SectorOffset(sectorSize, sector.Value);

            for (int index = 0; index < sectorSize / 4 && offset + index * 4 + 4 <= data.Length; index++)
                values.Add(ReadUInt32(data, offset + index * 4));

            sector = Next(sector.Value, fat);
        }

        return values.ToArray();
    }

    private static List<Entry> ReadDirectory(byte[] data, uint firstDirectory, uint[] fat, int sectorSize)
    {
        var entries = new List<Entry>();
        uint? sector = firstDirectory;
        int guard = 0;

        while (sector is not null && sector is not FreeSector and not NoStream)
        {
            if (++guard > MaxChainSectors || !SectorRange(sectorSize, sector.Value, data.Length))
                break;

            int offset = SectorOffset(sectorSize, sector.Value);

            for (int index = 0; index < sectorSize / EntrySize; index++)
            {
                int entryOffset = offset + index * EntrySize;
                if (entryOffset + EntrySize > data.Length) break;

                Entry? entry = ReadEntry(data, entryOffset);
                if (entry is not null) entries.Add(entry);
            }

            sector = Next(sector.Value, fat);
        }

        return entries;
    }

    private static Entry? ReadEntry(byte[] data, int offset)
    {
        int nameBytes = ReadUInt16(data, offset + 64);
        byte type = data[offset + 66];

        if (nameBytes is < 2 or > 64) return null;

        string name = Encoding.Unicode.GetString(data, offset, nameBytes - 2).TrimEnd('\0');

        return new Entry(
            name,
            type,
            ReadUInt32(data, offset + 116),
            (long)ReadUInt64(data, offset + 120));
    }

    // ---------- lectura de cadenas ----------

    private byte[]? ReadFatChain(uint startSector, long size) =>
        ReadFatChain(_data, startSector, size, _sectorSize, _fat);

    private static byte[] ReadFatChain(byte[] data, uint startSector, long size, int sectorSize, uint[] fat)
    {
        var buffer = new byte[size];
        int written = 0;
        uint? sector = startSector;
        int guard = 0;

        while (written < size && sector is not null && sector is not FreeSector and not NoStream)
        {
            if (++guard > MaxChainSectors || !SectorRange(sectorSize, sector.Value, data.Length))
                break;

            int offset = SectorOffset(sectorSize, sector.Value);
            int chunk = (int)Math.Min(sectorSize, size - written);
            chunk = Math.Min(chunk, data.Length - offset);

            if (chunk <= 0) break;

            Array.Copy(data, offset, buffer, written, chunk);
            written += chunk;

            sector = Next(sector.Value, fat);
        }

        return buffer;
    }

    private byte[]? ReadMiniStream(uint startSector, int size)
    {
        if (_miniStream is null) return null;

        var buffer = new byte[size];
        int written = 0;
        uint? sector = startSector;
        int guard = 0;

        while (written < size && sector is not null && sector is not FreeSector and not NoStream)
        {
            if (++guard > MaxChainSectors) break;

            int offset = MiniSectorOffset(_miniSectorSize, sector.Value);
            if (offset >= _miniStream.Length) break;

            int chunk = Math.Min(_miniSectorSize, Math.Min(size - written, _miniStream.Length - offset));
            if (chunk <= 0) break;

            Array.Copy(_miniStream, offset, buffer, written, chunk);
            written += chunk;

            sector = sector.Value < _miniFat.Length ? Sentinel(_miniFat[sector.Value]) : null;
        }

        return buffer;
    }

    private static uint? Next(uint sector, uint[] fat) =>
        sector < fat.Length ? Sentinel(fat[sector]) : null;

    private static uint? Sentinel(uint value) =>
        value is FreeSector or EndOfChain or NoStream ? null : value;

    private static bool SectorRange(int sectorSize, uint sector, int dataLength) =>
        SectorOffset(sectorSize, sector) + 4 <= dataLength;

    private static int SectorOffset(int sectorSize, uint sector) => 512 + (int)sector * sectorSize;

    private static int MiniSectorOffset(int miniSectorSize, uint sector) => (int)sector * miniSectorSize;

    private static ulong ReadUInt64(byte[] data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

    private static uint ReadUInt32(byte[] data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static int ReadUInt16(byte[] data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    /// <summary>Entrada de directorio: 0 desconocido, 1 almacenamiento, 2 secuencia, 5 raíz.</summary>
    private sealed record Entry(string Name, byte Type, uint StartSector, long Size);
}
