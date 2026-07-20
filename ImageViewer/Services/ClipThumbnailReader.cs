using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ImageViewer.Services;

/// <summary>
/// Extracts the embedded canvas preview thumbnail (a plain PNG blob) from Clip Studio Paint
/// (.clip) files. A .clip file starts with a 24-byte outer header ("CSFCHUNK" (8) + big-endian
/// total file size (8) + an 8-byte field of unknown purpose, always observed as 24 - verified
/// against a real .clip file's raw bytes), followed by a sequence of
/// [8-byte ASCII signature][8-byte big-endian length][payload] chunks. One chunk, "CHNKSQLi",
/// contains a real embedded SQLite3 database whose "CanvasPreview" table holds the thumbnail PNG
/// in its "ImageData" column. We walk chunks and seek past everything except CHNKSQLi so large
/// layer-data chunks (CHNKExta) are never read into memory.
/// </summary>
public static class ClipThumbnailReader
{
    private const string SqliteChunkSignature = "CHNKSQLi";
    private const int OuterHeaderSize = 24; // "CSFCHUNK" (8) + big-endian file size (8) + unknown (8)
    private const int MaxChunksToScan = 100_000;

    public static byte[]? ExtractPreviewPng(string clipFilePath)
    {
        var sqliteBytes = ExtractSqliteChunk(clipFilePath);
        if (sqliteBytes == null)
            return null;

        var tempDbPath = Path.Combine(Path.GetTempPath(), $"clipviewer_{Guid.NewGuid():N}.sqlite");
        try
        {
            File.WriteAllBytes(tempDbPath, sqliteBytes);

            using var connection = new SqliteConnection($"Data Source={tempDbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ImageData FROM CanvasPreview ORDER BY MainId DESC LIMIT 1";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
                return null;

            return (byte[])reader.GetValue(0);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(tempDbPath); } catch { /* best effort cleanup */ }
        }
    }

    private static byte[]? ExtractSqliteChunk(string clipFilePath)
    {
        using var stream = new FileStream(clipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        if (stream.Length < OuterHeaderSize)
            return null;

        stream.Seek(OuterHeaderSize, SeekOrigin.Begin);

        var signatureBuffer = new byte[8];
        var lengthBuffer = new byte[8];

        for (var i = 0; i < MaxChunksToScan; i++)
        {
            if (stream.Position + 16 > stream.Length)
                return null;

            if (!ReadExact(stream, signatureBuffer))
                return null;
            if (!ReadExact(stream, lengthBuffer))
                return null;

            var signature = Encoding.ASCII.GetString(signatureBuffer);
            var payloadLength = ReadInt64BigEndian(lengthBuffer);

            if (payloadLength < 0 || stream.Position + payloadLength > stream.Length)
                return null;

            if (signature == SqliteChunkSignature)
            {
                var payload = new byte[payloadLength];
                return ReadExact(stream, payload) ? payload : null;
            }

            stream.Seek(payloadLength, SeekOrigin.Current);
        }

        return null;
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private static long ReadInt64BigEndian(byte[] data)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | data[i];
        return value;
    }
}
