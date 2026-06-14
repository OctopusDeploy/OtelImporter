namespace OtelImporter.Input;

// Opens an input trace file as a forward-only stream of decompressed bytes.
// Abstracted behind an interface so the import pipeline can be unit-tested with
// in-memory inputs.
internal interface IInputStreamFactory
{
    Stream Open(string path);
}
