using System.Text;

namespace Spyware.UnitTests;

/// <summary>
/// Escribe archivos MS-CFB mínimos para probar el lector de StickyNotes.snt sin depender
/// de que la máquina tenga la versión clásica del programa: un sector FAT, una mini FAT,
/// el directorio y las secuencias. Es el mismo trazado que genera la aplicación real,
/// solo que sin transacciones ni propiedades.
/// </summary>
internal static class CompoundFileFixture
{
    private const uint Free = 0xFFFFFFFF;
    private const uint End = 0xFFFFFFFE;
    private const int Sector = 512;
    private const int MiniSector = 64;
    private const int MiniCutOff = 4096;
    private const int EntrySize = 128;

    public static void Write(string path, IReadOnlyList<(string Name, byte[] Data)> streams)
    {
        List<(string Name, byte[] Data)> small = streams.Where(s => s.Data.Length < MiniCutOff).ToList();
        List<(string Name, byte[] Data)> large = streams.Where(s => s.Data.Length >= MiniCutOff).ToList();

        // 1. La "mini secuencia" del raíz guarda las secuencias pequeñas, alineadas a 64 bytes.
        var miniStream = new List<byte>();
        var miniChains = new Dictionary<string, List<uint>>();

        foreach ((string name, byte[] data) in small)
        {
            int firstMini = miniStream.Count / MiniSector;
            int miniCount = Math.Max(1, (data.Length + MiniSector - 1) / MiniSector);

            miniChains[name] = Enumerable.Range(firstMini, miniCount).Select(index => (uint)index).ToList();

            for (int index = 0; index < miniCount * MiniSector; index++)
                miniStream.Add(index < data.Length ? data[index] : (byte)0);
        }

        // 2. Reparto de sectores: FAT, mini FAT, directorio y después los datos.
        int directoryEntries = streams.Count + 1;
        int directorySectors = (directoryEntries * EntrySize + Sector - 1) / Sector;

        uint fatSector = 0;
        uint miniFatSector = 1;
        uint firstDirectory = 2;
        uint nextFree = firstDirectory + (uint)directorySectors;

        var fat = new Dictionary<uint, uint>();

        List<uint> MiniStreamChain = Allocate((uint)nextFree, (miniStream.Count + Sector - 1) / Sector);
        nextFree += (uint)MiniStreamChain.Count;

        var largeChains = new Dictionary<string, List<uint>>();

        foreach ((string name, byte[] data) in large)
        {
            List<uint> chain = Allocate(nextFree, (data.Length + Sector - 1) / Sector);
            nextFree += (uint)chain.Count;
            largeChains[name] = chain;
        }

        uint lastSector = Math.Max(nextFree, miniFatSector + 1);

        for (uint sector = 0; sector < lastSector; sector++)
            fat[sector] = Free;

        fat[fatSector] = End;
        fat[miniFatSector] = End;

        for (int index = 0; index < directorySectors; index++)
            fat[firstDirectory + (uint)index] = index == directorySectors - 1 ? End : firstDirectory + (uint)(index + 1);

        Link(fat, MiniStreamChain);

        foreach (List<uint> chain in largeChains.Values)
            Link(fat, chain);

        // 3. Mini FAT: una entrada por mini-sector de la mini secuencia.
        var miniFat = new List<uint>();

        foreach ((string name, _) in small)
        {
            foreach (uint mini in miniChains[name])
            {
                while (miniFat.Count <= mini) miniFat.Add(Free);
                miniFat[(int)mini] = End;
            }
        }

        foreach (List<uint> chain in miniChains.Values)
        {
            for (int index = 0; index < chain.Count - 1; index++)
                miniFat[(int)chain[index]] = chain[index + 1];
        }

        while (miniFat.Count < Sector / 4) miniFat.Add(Free);

        // 4. Cuerpo del archivo.
        using var file = File.Create(path);

        WriteHeader(file, fatSector, miniFatSector, firstDirectory);

        WriteSector(file, BuildFatSector(fat));
        WriteSector(file, BuildMiniFatSector(miniFat));

        var directory = new List<byte[]>
        {
            Entry("Root Entry", 5, MiniStreamChain.Count == 0 ? Free : MiniStreamChain[0],
                (ulong)miniStream.Count, streams.Count == 0 ? Free : 1)
        };

        for (int index = 0; index < streams.Count; index++)
        {
            (string name, byte[] data) = streams[index];

            uint start = largeChains.TryGetValue(name, out List<uint>? largeChain)
                ? largeChain[0]
                : miniChains.TryGetValue(name, out List<uint>? smallChain)
                    ? smallChain[0]
                    : Free;

            ulong size = (ulong)data.Length;

            directory.Add(Entry(name, 2, start, size,
                index + 1 < streams.Count ? (uint)(index + 2) : Free));
        }

        while (directory.Count % (Sector / EntrySize) != 0)
            directory.Add(new byte[EntrySize]);

        // Cuatro entradas de 128 bytes por sector: se agrupan antes de escribir.
        int entriesPerSector = Sector / EntrySize;

        for (int start = 0; start < directory.Count; start += entriesPerSector)
        {
            var block = new byte[entriesPerSector * EntrySize];

            for (int index = 0; index < entriesPerSector; index++)
                directory[start + index].CopyTo(block, index * EntrySize);

            WriteSector(file, block);
        }

        if (miniStream.Count > 0)
            WriteSector(file, miniStream.ToArray());

        foreach ((string name, byte[] data) in large)
            WriteSector(file, data);

        file.Flush();
    }

    private static List<uint> Allocate(uint first, int count) =>
        Enumerable.Range(0, count).Select(index => first + (uint)index).ToList();

    private static void Link(Dictionary<uint, uint> fat, List<uint> chain)
    {
        for (int index = 0; index < chain.Count; index++)
            fat[chain[index]] = index == chain.Count - 1 ? End : chain[index + 1];
    }

    private static void WriteHeader(Stream file, uint fatSector, uint miniFatSector, uint firstDirectory)
    {
        var header = new byte[Sector];

        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        Array.Copy(signature, 0, header, 0, 8);

        WriteUInt16(header, 24, 0x003E);   // versión menor
        WriteUInt16(header, 26, 0x0003);   // versión mayor 3 → sectores de 512
        WriteUInt16(header, 28, 0xFFFE);   // orden little-endian
        WriteUInt16(header, 30, 9);        // desplazamiento de sector
        WriteUInt16(header, 32, 6);        // desplazamiento de mini-sector
        WriteUInt32(header, 44, 1);        // sectores FAT
        WriteUInt32(header, 48, firstDirectory);
        WriteUInt32(header, 56, MiniCutOff);
        WriteUInt32(header, 60, miniFatSector);
        WriteUInt32(header, 64, 1);        // sectores de mini FAT
        WriteUInt32(header, 68, Free);     // sin cadena DIFAT
        WriteUInt32(header, 72, 0);

        for (int index = 0; index < 109; index++)
            WriteUInt32(header, 76 + index * 4, index == 0 ? fatSector : Free);

        file.Write(header, 0, header.Length);
    }

    private static byte[] BuildFatSector(Dictionary<uint, uint> fat)
    {
        var sector = new byte[Sector];

        for (int index = 0; index < Sector / 4; index++)
            WriteUInt32(sector, index * 4, fat.TryGetValue((uint)index, out uint value) ? value : Free);

        return sector;
    }

    private static byte[] BuildMiniFatSector(List<uint> miniFat)
    {
        var sector = new byte[Sector];

        for (int index = 0; index < Sector / 4; index++)
            WriteUInt32(sector, index * 4, index < miniFat.Count ? miniFat[index] : Free);

        return sector;
    }

    private static byte[] Entry(string name, byte type, uint start, ulong size, uint siblingOrChild)
    {
        var entry = new byte[EntrySize];

        byte[] encoded = Encoding.Unicode.GetBytes(name + "\0");
        Array.Copy(encoded, 0, entry, 0, encoded.Length);
        WriteUInt16(entry, 64, (ushort)encoded.Length);

        entry[66] = type;
        entry[67] = 1;   // negro

        WriteUInt32(entry, 68, Free);                  // hermano izquierdo
        WriteUInt32(entry, 72, type == 2 ? siblingOrChild : Free); // hermano derecho (hermanos en el árbol)
        WriteUInt32(entry, 76, type == 5 ? siblingOrChild : Free); // primer hijo (solo almacenamiento/raíz)
        WriteUInt32(entry, 116, start);
        WriteUInt64(entry, 120, size);

        return entry;
    }

    private static void WriteSector(Stream file, byte[] data)
    {
        var sector = new byte[((data.Length + Sector - 1) / Sector) * Sector];
        Array.Copy(data, 0, sector, 0, data.Length);
        file.Write(sector, 0, sector.Length);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), value);
}
