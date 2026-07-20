namespace ImageViewer.Models;

public sealed class ImageFileItem
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
}
