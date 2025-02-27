// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module internal FSharp.Compiler.AbstractIL.ILPdbWriter

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.IO.Compression
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Security.Cryptography
open System.Text
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.Support
open Internal.Utilities.Library
open Internal.Utilities.Library.Extras
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.IO
open FSharp.Compiler.Text.Range

type BlobBuildingStream() =
    inherit Stream()

    static let chunkSize = 32 * 1024
    let builder = BlobBuilder(chunkSize)

    override _.CanWrite = true

    override _.CanRead = false

    override _.CanSeek = false

    override _.Length = int64 builder.Count

    override _.Write(buffer: byte array, offset: int, count: int) =
        builder.WriteBytes(buffer, offset, count)

    override _.WriteByte(value: byte) = builder.WriteByte value

    member _.WriteInt32(value: int) = builder.WriteInt32 value

    member _.ToImmutableArray() = builder.ToImmutableArray()

    member _.TryWriteBytes(stream: Stream, length: int) = builder.TryWriteBytes(stream, length)

    override _.Flush() = ()

    override _.Dispose(_disposing: bool) = ()

    override _.Seek(_offset: int64, _origin: SeekOrigin) = raise (NotSupportedException())

    override _.Read(_buffer: byte array, _offset: int, _count: int) = raise (NotSupportedException())

    override _.SetLength(_value: int64) = raise (NotSupportedException())
    override val Position = 0L with get, set

// --------------------------------------------------------------------
// PDB types
// --------------------------------------------------------------------
type PdbDocumentData = ILSourceDocument

type PdbLocalVar =
    {
        Name: string
        Signature: byte[]
        /// the local index the name corresponds to
        Index: int32
    }

type PdbImport =
    | ImportType of targetTypeToken: int32 (* alias: string option *)
    | ImportNamespace of targetNamespace: string (* assembly: ILAssemblyRef option * alias: string option *)
//| ReferenceAlias of string
//| OpenXmlNamespace of prefix: string * xmlNamespace: string

type PdbImports =
    {
        Parent: PdbImports option
        Imports: PdbImport[]
    }

type PdbMethodScope =
    {
        Children: PdbMethodScope[]
        StartOffset: int
        EndOffset: int
        Locals: PdbLocalVar[]
        Imports: PdbImports option
    }

type PdbSourceLoc =
    {
        Document: int
        Line: int
        Column: int
    }

type PdbDebugPoint =
    {
        Document: int
        Offset: int
        Line: int
        Column: int
        EndLine: int
        EndColumn: int
    }

    override x.ToString() =
        sprintf "(%d,%d)-(%d,%d)" x.Line x.Column x.EndLine x.EndColumn

type PdbMethodData =
    {
        MethToken: int32
        MethName: string
        LocalSignatureToken: int32
        Params: PdbLocalVar array
        RootScope: PdbMethodScope option
        DebugRange: (PdbSourceLoc * PdbSourceLoc) option
        DebugPoints: PdbDebugPoint array
    }

module SequencePoint =
    let orderBySource sp1 sp2 =
        let c1 = compare sp1.Document sp2.Document

        if c1 <> 0 then
            c1
        else
            let c1 = compare sp1.Line sp2.Line

            if c1 <> 0 then c1 else compare sp1.Column sp2.Column

    let orderByOffset sp1 sp2 = compare sp1.Offset sp2.Offset

/// 28 is the size of the IMAGE_DEBUG_DIRECTORY in ntimage.h
let sizeof_IMAGE_DEBUG_DIRECTORY = 28

[<NoEquality; NoComparison>]
type PdbData =
    {
        EntryPoint: int32 option
        Timestamp: int32
        ModuleID: byte[]
        Documents: PdbDocumentData[]
        Methods: PdbMethodData[]
        TableRowCounts: int[]
    }

type BinaryChunk = { size: int32; addr: int32 }

type idd =
    {
        iddCharacteristics: int32
        iddMajorVersion: int32 (* actually u16 in IMAGE_DEBUG_DIRECTORY *)
        iddMinorVersion: int32 (* actually u16 in IMAGE_DEBUG_DIRECTORY *)
        iddType: int32
        iddTimestamp: int32
        iddData: byte[]
        iddChunk: BinaryChunk
    }

/// The specified Hash algorithm to use on portable pdb files.
type HashAlgorithm =
    | Sha1
    | Sha256

// Document checksum algorithms
let guidSha1 = Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460")
let guidSha2 = Guid("8829d00f-11b8-4213-878b-770e8597ac16")

let checkSum (url: string) (checksumAlgorithm: HashAlgorithm) =
    try
        use file = FileSystem.OpenFileForReadShim(url)

        let guid, alg =
            match checksumAlgorithm with
            | HashAlgorithm.Sha1 -> guidSha1, SHA1.Create() :> System.Security.Cryptography.HashAlgorithm
            | HashAlgorithm.Sha256 -> guidSha2, SHA256.Create() :> System.Security.Cryptography.HashAlgorithm

        let checkSum = alg.ComputeHash file
        Some(guid, checkSum)
    with _ ->
        None

//---------------------------------------------------------------------
// Portable PDB Writer
//---------------------------------------------------------------------

let b0 n = (n &&& 0xFF)
let b1 n = ((n >>> 8) &&& 0xFF)
let b2 n = ((n >>> 16) &&& 0xFF)
let b3 n = ((n >>> 24) &&& 0xFF)

let i32AsBytes i =
    [| byte (b0 i); byte (b1 i); byte (b2 i); byte (b3 i) |]

let cvMagicNumber = 0x53445352L

let pdbGetCvDebugInfo (mvid: byte[]) (timestamp: int32) (filepath: string) (cvChunk: BinaryChunk) =
    let iddCvBuffer =
        // Debug directory entry
        let path = (Encoding.UTF8.GetBytes filepath)

        let buffer =
            Array.zeroCreate (sizeof<int32> + mvid.Length + sizeof<int32> + path.Length + 1)

        let offset, size = (0, sizeof<int32>) // Magic Number RSDS dword: 0x53445352L
        Buffer.BlockCopy(i32AsBytes (int cvMagicNumber), 0, buffer, offset, size)
        let offset, size = (offset + size, mvid.Length) // mvid Guid
        Buffer.BlockCopy(mvid, 0, buffer, offset, size)
        let offset, size = (offset + size, sizeof<int32>) // # of pdb files generated (1)
        Buffer.BlockCopy(i32AsBytes 1, 0, buffer, offset, size)
        let offset, size = (offset + size, path.Length) // Path to pdb string
        Buffer.BlockCopy(path, 0, buffer, offset, size)
        buffer

    {
        iddCharacteristics = 0 // Reserved
        iddMajorVersion = 0x0100 // VersionMajor should be 0x0100
        iddMinorVersion = 0x504d // VersionMinor should be 0x504d
        iddType = 2 // IMAGE_DEBUG_TYPE_CODEVIEW
        iddTimestamp = timestamp
        iddData = iddCvBuffer // Path name to the pdb file when built
        iddChunk = cvChunk
    }

let pdbMagicNumber = 0x4244504dL

let pdbGetEmbeddedPdbDebugInfo (embeddedPdbChunk: BinaryChunk) (uncompressedLength: int64) (compressedStream: MemoryStream) =
    let iddPdbBuffer =
        let buffer =
            Array.zeroCreate (sizeof<int32> + sizeof<int32> + int (compressedStream.Length))

        let offset, size = (0, sizeof<int32>) // Magic Number dword: 0x4244504dL
        Buffer.BlockCopy(i32AsBytes (int pdbMagicNumber), 0, buffer, offset, size)
        let offset, size = (offset + size, sizeof<int32>) // Uncompressed size
        Buffer.BlockCopy(i32AsBytes (int uncompressedLength), 0, buffer, offset, size)
        let offset, size = (offset + size, int (compressedStream.Length)) // Uncompressed size
        Buffer.BlockCopy(compressedStream.ToArray(), 0, buffer, offset, size)
        buffer

    {
        iddCharacteristics = 0 // Reserved
        iddMajorVersion = 0x0100 // VersionMajor should be 0x0100
        iddMinorVersion = 0x0100 // VersionMinor should be 0x0100
        iddType = 17 // IMAGE_DEBUG_TYPE_EMBEDDEDPDB
        iddTimestamp = 0
        iddData = iddPdbBuffer // Path name to the pdb file when built
        iddChunk = embeddedPdbChunk
    }

let pdbChecksumDebugInfo timestamp (checksumPdbChunk: BinaryChunk) (algorithmName: string) (checksum: byte[]) =
    let iddBuffer =
        let alg = Encoding.UTF8.GetBytes(algorithmName)
        let buffer = Array.zeroCreate (alg.Length + 1 + checksum.Length)
        Buffer.BlockCopy(alg, 0, buffer, 0, alg.Length)
        Buffer.BlockCopy(checksum, 0, buffer, alg.Length + 1, checksum.Length)
        buffer

    {
        iddCharacteristics = 0 // Reserved
        iddMajorVersion = 1 // VersionMajor should be 1
        iddMinorVersion = 0 // VersionMinor should be 0
        iddType = 19 // IMAGE_DEBUG_TYPE_CHECKSUMPDB
        iddTimestamp = timestamp
        iddData = iddBuffer // Path name to the pdb file when built
        iddChunk = checksumPdbChunk
    }

let pdbGetPdbDebugDeterministicInfo (deterministicPdbChunk: BinaryChunk) =
    {
        iddCharacteristics = 0 // Reserved
        iddMajorVersion = 0 // VersionMajor should be 0
        iddMinorVersion = 0 // VersionMinor should be 00
        iddType = 16 // IMAGE_DEBUG_TYPE_DETERMINISTIC
        iddTimestamp = 0
        iddData = Array.empty // No DATA
        iddChunk = deterministicPdbChunk
    }

let pdbGetDebugInfo
    (contentId: byte[])
    (timestamp: int32)
    (filepath: string)
    (cvChunk: BinaryChunk)
    (embeddedPdbChunk: BinaryChunk option)
    (deterministicPdbChunk: BinaryChunk)
    (checksumPdbChunk: BinaryChunk)
    (algorithmName: string)
    (checksum: byte[])
    (uncompressedLength: int64)
    (compressedStream: MemoryStream option)
    (embeddedPdb: bool)
    (deterministic: bool)
    =
    [|
        yield pdbGetCvDebugInfo contentId timestamp filepath cvChunk
        yield pdbChecksumDebugInfo timestamp checksumPdbChunk algorithmName checksum
        if embeddedPdb then
            match compressedStream, embeddedPdbChunk with
            | None, _
            | _, None -> ()
            | Some compressedStream, Some chunk -> yield pdbGetEmbeddedPdbDebugInfo chunk uncompressedLength compressedStream
        if deterministic then
            yield pdbGetPdbDebugDeterministicInfo deterministicPdbChunk
    |]

//------------------------------------------------------------------------------
// PDB Writer.  The function [WritePdbInfo] abstracts the
// imperative calls to the Symbol Writer API.
//------------------------------------------------------------------------------

// This function takes output file name and returns debug file name.
let getDebugFileName outfile =
    (FileSystemUtils.chopExtension outfile) + ".pdb"

let sortMethods info =
    reportTime (sprintf "PDB: Defined %d documents" info.Documents.Length)
    Array.sortInPlaceBy (fun x -> x.MethToken) info.Methods
    reportTime (sprintf "PDB: Sorted %d methods" info.Methods.Length)
    ()

let getRowCounts tableRowCounts =
    let builder = ImmutableArray.CreateBuilder<int>(tableRowCounts |> Array.length)
    tableRowCounts |> Seq.iter (builder.Add)
    builder.MoveToImmutable()

let scopeSorter (scope1: PdbMethodScope) (scope2: PdbMethodScope) =
    if scope1.StartOffset > scope2.StartOffset then
        1
    elif scope1.StartOffset < scope2.StartOffset then
        -1
    elif (scope1.EndOffset - scope1.StartOffset) > (scope2.EndOffset - scope2.StartOffset) then
        -1
    elif (scope1.EndOffset - scope1.StartOffset) < (scope2.EndOffset - scope2.StartOffset) then
        1
    else
        0

type PortablePdbGenerator
    (embedAllSource: bool, embedSourceList: string list, sourceLink: string, checksumAlgorithm, info: PdbData, pathMap: PathMap) =

    let docs = info.Documents

    // The metadata to wite to the PoortablePDB (Roslyn = _debugMetadataOpt)

    let metadata = MetadataBuilder()

    let serializeDocumentName (name: string) =
        let name = PathMap.apply pathMap name

        let count s c =
            s |> Seq.filter (fun ch -> c = ch) |> Seq.length

        let s1, s2 = '/', '\\'

        let separator = if (count name s1) >= (count name s2) then s1 else s2

        let writer = BlobBuilder()
        writer.WriteByte(byte separator)

        for part in name.Split([| separator |]) do
            let partIndex =
                MetadataTokens.GetHeapOffset(BlobHandle.op_Implicit (metadata.GetOrAddBlobUTF8 part))

            writer.WriteCompressedInteger(int partIndex)

        metadata.GetOrAddBlob writer

    let corSymLanguageTypeId =
        Guid(0xAB4F38C9u, 0xB6E6us, 0x43baus, 0xBEuy, 0x3Buy, 0x58uy, 0x08uy, 0x0Buy, 0x2Cuy, 0xCCuy, 0xE3uy)

    let embeddedSourceId =
        Guid(0x0e8a571bu, 0x6926us, 0x466eus, 0xb4uy, 0xaduy, 0x8auy, 0xb0uy, 0x46uy, 0x11uy, 0xf5uy, 0xfeuy)

    let sourceLinkId =
        Guid(0xcc110556u, 0xa091us, 0x4d38us, 0x9fuy, 0xecuy, 0x25uy, 0xabuy, 0x9auy, 0x35uy, 0x1auy, 0x6auy)

    /// <summary>
    /// The maximum number of bytes in to write out uncompressed.
    ///
    /// This prevents wasting resources on compressing tiny files with little to negative gain
    /// in PDB file size.
    ///
    /// Chosen as the point at which we start to see > 10% blob size reduction using all
    /// current source files in corefx and roslyn as sample data.
    /// </summary>
    let sourceCompressionThreshold = 200

    let includeSource (file: string) =
        let isInList =
            embedSourceList
            |> List.exists (fun f -> String.Compare(file, f, StringComparison.OrdinalIgnoreCase) = 0)

        if not embedAllSource && not isInList || not (FileSystem.FileExistsShim file) then
            None
        else
            use stream = FileSystem.OpenFileForReadShim(file)

            let length64 = stream.Length

            if length64 > int64 Int32.MaxValue then
                raise (IOException("File is too long"))

            let builder = new BlobBuildingStream()
            let length = int length64

            if length < sourceCompressionThreshold then
                builder.WriteInt32 0
                builder.TryWriteBytes(stream, length) |> ignore
            else
                builder.WriteInt32 length
                use deflater = new DeflateStream(builder, CompressionMode.Compress, true)
                stream.CopyTo deflater

            Some(builder.ToImmutableArray())

    let documentIndex =
        let mutable index = Dictionary<string, DocumentHandle>(docs.Length)

        let docLength = docs.Length + if String.IsNullOrEmpty sourceLink then 1 else 0

        metadata.SetCapacity(TableIndex.Document, docLength)

        for doc in docs do
            // For F# Interactive, file name 'stdin' gets generated for interactive inputs
            let handle =
                match checkSum doc.File checksumAlgorithm with
                | Some(hashAlg, checkSum) ->
                    let dbgInfo =
                        (serializeDocumentName doc.File,
                         metadata.GetOrAddGuid hashAlg,
                         metadata.GetOrAddBlob(checkSum.ToImmutableArray()),
                         metadata.GetOrAddGuid corSymLanguageTypeId)
                        |> metadata.AddDocument

                    match includeSource doc.File with
                    | None -> ()
                    | Some blob ->
                        metadata.AddCustomDebugInformation(
                            DocumentHandle.op_Implicit dbgInfo,
                            metadata.GetOrAddGuid embeddedSourceId,
                            metadata.GetOrAddBlob blob
                        )
                        |> ignore

                    dbgInfo
                | None ->
                    let dbgInfo =
                        (serializeDocumentName doc.File,
                         metadata.GetOrAddGuid(Guid.Empty),
                         metadata.GetOrAddBlob(ImmutableArray<byte>.Empty),
                         metadata.GetOrAddGuid corSymLanguageTypeId)
                        |> metadata.AddDocument

                    dbgInfo

            index.Add(doc.File, handle)

        if not (String.IsNullOrWhiteSpace sourceLink) then
            use fs = FileSystem.OpenFileForReadShim(sourceLink)
            use ms = new MemoryStream()
            fs.CopyTo ms

            metadata.AddCustomDebugInformation(
                ModuleDefinitionHandle.op_Implicit (EntityHandle.ModuleDefinition),
                metadata.GetOrAddGuid sourceLinkId,
                metadata.GetOrAddBlob(ms.ToArray())
            )
            |> ignore

        index

    let mutable lastLocalVariableHandle = Unchecked.defaultof<LocalVariableHandle>

    let getDocumentHandle d =
        if docs.Length = 0 || d < 0 || d > docs.Length then
            Unchecked.defaultof<DocumentHandle>
        else
            match documentIndex.TryGetValue(docs[d].File) with
            | false, _ -> Unchecked.defaultof<DocumentHandle>
            | true, h -> h

    let moduleImportScopeHandle = MetadataTokens.ImportScopeHandle(1)
    let importScopesTable = new Dictionary<PdbImports, ImportScopeHandle>()

    let serializeImport (writer: BlobBuilder) (import: PdbImport) =
        match import with
        // We don't yet emit these kinds of imports
        //| AssemblyReferenceAlias alias->
        //    // <import> ::= AliasAssemblyReference <alias> <target-assembly>
        //    writer.WriteByte((byte)ImportDefinitionKind.AliasAssemblyReference);
        //    writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(alias.Name)));
        //    writer.WriteCompressedInteger(MetadataTokens.GetRowNumber(GetOrAddAssemblyReferenceHandle(alias.Assembly)));

        //| OpenXmlNamespace(prefix, xmlNamespace) ->
        //        Debug.Assert(import.TargetNamespaceOpt == null);
        //        Debug.Assert(import.TargetAssemblyOpt == null);
        //        Debug.Assert(import.TargetTypeOpt == null);

        //        // <import> ::= ImportXmlNamespace <alias> <target-namespace>
        //        writer.WriteByte((byte)ImportDefinitionKind.ImportXmlNamespace);
        //        writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.AliasOpt)));
        //        writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.TargetXmlNamespaceOpt)));
        // Corresponds to an 'open <module>' or 'open type' in F#
        | ImportType targetTypeToken ->

            //if (import.AliasOpt != null)
            //{
            //    // <import> ::= AliasType <alias> <target-type>
            //    writer.WriteByte((byte)ImportDefinitionKind.AliasType);
            //    writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.AliasOpt)));
            //}
            //else
            // <import> ::= ImportType <target-type>
            writer.WriteByte(byte ImportDefinitionKind.ImportType)

            writer.WriteCompressedInteger(targetTypeToken)

        // Corresponds to an 'open <namespace>'
        | ImportNamespace targetNamespace ->
            //if (import.TargetAssemblyOpt != null)
            //{
            //    if (import.AliasOpt != null)
            //    {
            //        // <import> ::= AliasAssemblyNamespace <alias> <target-assembly> <target-namespace>
            //        writer.WriteByte((byte)ImportDefinitionKind.AliasAssemblyNamespace);
            //        writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.AliasOpt)));
            //    }
            //    else
            //    {
            //        // <import> ::= ImportAssemblyNamespace <target-assembly> <target-namespace>
            //        writer.WriteByte((byte)ImportDefinitionKind.ImportAssemblyNamespace);
            //    }

            //    writer.WriteCompressedInteger(MetadataTokens.GetRowNumber(GetAssemblyReferenceHandle(import.TargetAssemblyOpt)));
            //}
            //else
            //{
            //if (import.AliasOpt != null)
            //{
            //    // <import> ::= AliasNamespace <alias> <target-namespace>
            //    writer.WriteByte((byte)ImportDefinitionKind.AliasNamespace);
            //    writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.AliasOpt)));
            //}
            //else
            //{
            // <import> ::= ImportNamespace <target-namespace>
            writer.WriteByte(byte ImportDefinitionKind.ImportNamespace)
            writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(metadata.GetOrAddBlobUTF8(targetNamespace)))

    //| ReferenceAlias alias ->
    //        // <import> ::= ImportReferenceAlias <alias>
    //        Debug.Assert(import.AliasOpt != null);
    //        Debug.Assert(import.TargetAssemblyOpt == null);

    //        writer.WriteByte((byte)ImportDefinitionKind.ImportAssemblyReferenceAlias);
    //        writer.WriteCompressedInteger(MetadataTokens.GetHeapOffset(_debugMetadataOpt.GetOrAddBlobUTF8(import.AliasOpt)));

    let serializeImportsBlob (imports: PdbImport[]) =
        let writer = new BlobBuilder()

        for import in imports do
            serializeImport writer import

        metadata.GetOrAddBlob(writer)

    // Define the empty global imports scope for the whole assembly,it gets index #1 (the first entry in the table)
    let defineModuleImportScope () =
        let writer = new BlobBuilder()
        let blob = metadata.GetOrAddBlob writer

        let rid =
            metadata.AddImportScope(parentScope = Unchecked.defaultof<_>, imports = blob)

        assert (rid = moduleImportScopeHandle)

    let rec getImportScopeIndex (imports: PdbImports) =
        match importScopesTable.TryGetValue(imports) with
        | true, v -> v
        | _ ->

            let parentScopeHandle =
                match imports.Parent with
                | None -> moduleImportScopeHandle
                | Some parent -> getImportScopeIndex parent

            let blob = serializeImportsBlob imports.Imports
            let result = metadata.AddImportScope(parentScopeHandle, blob)

            importScopesTable.Add(imports, result)
            result

    let flattenScopes rootScope =
        let list = List<PdbMethodScope>()

        let rec flattenScopes scope parent =

            list.Add scope

            for nestedScope in scope.Children do
                let isNested =
                    match parent with
                    | Some p -> nestedScope.StartOffset >= p.StartOffset && nestedScope.EndOffset <= p.EndOffset
                    | None -> true

                flattenScopes nestedScope (if isNested then Some scope else parent)

        flattenScopes rootScope None

        list.ToArray() |> Array.sortWith<PdbMethodScope> scopeSorter

    let writeMethodScopes methToken rootScope =

        let flattenedScopes = flattenScopes rootScope

        for scope in flattenedScopes do
            // Get or create the import scope for this method
            let importScopeHandle =

                match scope.Imports with
                | None -> Unchecked.defaultof<_>
                | Some imports -> getImportScopeIndex imports

            let lastRowNumber =
                MetadataTokens.GetRowNumber(LocalVariableHandle.op_Implicit lastLocalVariableHandle)

            let nextHandle = MetadataTokens.LocalVariableHandle(lastRowNumber + 1)

            metadata.AddLocalScope(
                MetadataTokens.MethodDefinitionHandle(methToken),
                importScopeHandle,
                nextHandle,
                Unchecked.defaultof<LocalConstantHandle>,
                scope.StartOffset,
                scope.EndOffset - scope.StartOffset
            )
            |> ignore

            for localVariable in scope.Locals do
                lastLocalVariableHandle <-
                    metadata.AddLocalVariable(
                        LocalVariableAttributes.None,
                        localVariable.Index,
                        metadata.GetOrAddString(localVariable.Name)
                    )

    let emitMethod minfo =
        let docHandle, sequencePointBlob =
            let sps =
                match minfo.DebugRange with
                | None -> Array.empty
                | Some _ -> minfo.DebugPoints

            let builder = BlobBuilder()
            builder.WriteCompressedInteger(minfo.LocalSignatureToken)

            if sps.Length = 0 then
                builder.WriteCompressedInteger(0)
                builder.WriteCompressedInteger(0)
                Unchecked.defaultof<DocumentHandle>, Unchecked.defaultof<BlobHandle>
            else

                // Return a document that the entire method body is declared within.
                // If part of the method body is in another document returns nil handle.
                let tryGetSingleDocumentIndex =
                    let mutable singleDocumentIndex = sps[0].Document

                    for i in 1 .. sps.Length - 1 do
                        if sps[i].Document <> singleDocumentIndex then
                            singleDocumentIndex <- -1

                    singleDocumentIndex

                // Initial document:  When sp's spread over more than one document we put the initial document here.
                let singleDocumentIndex = tryGetSingleDocumentIndex

                if singleDocumentIndex = -1 then
                    builder.WriteCompressedInteger(
                        MetadataTokens.GetRowNumber(DocumentHandle.op_Implicit (getDocumentHandle sps[0].Document))
                    )

                let mutable previousNonHiddenStartLine = -1
                let mutable previousNonHiddenStartColumn = 0

                for i in 0 .. (sps.Length - 1) do

                    if singleDocumentIndex <> -1 && sps[i].Document <> singleDocumentIndex then
                        builder.WriteCompressedInteger(0)

                        builder.WriteCompressedInteger(
                            MetadataTokens.GetRowNumber(DocumentHandle.op_Implicit (getDocumentHandle sps[i].Document))
                        )
                    else
                        //=============================================================================================================================================
                        // Sequence-point-record
                        // Validate these with magic numbers according to the portable pdb spec Sequence point dexcription:
                        // https://github.com/dotnet/corefx/blob/master/src/System.Reflection.Metadata/specs/PortablePdb-Metadata.md#methoddebuginformation-table-0x31
                        //
                        // So the spec is actually bit iffy!!!!! (More like guidelines really.  )
                        //  It uses code similar to this to validate the values
                        //    if (result < 0 || result >= ushort.MaxValue)  // be errorfull
                        // Spec Says 0x10000 and value max = 0xFFFF but it can't even be = to maxvalue, and so the range is 0 .. 0xfffe inclusive
                        //=============================================================================================================================================

                        let capValue v maxValue =
                            if v < 0 then 0
                            elif v > maxValue then maxValue
                            else v

                        let capOffset v = capValue v 0xfffe
                        let capLine v = capValue v 0x1ffffffe
                        let capColumn v = capValue v 0xfffe

                        let offset = capOffset sps[i].Offset
                        let startLine = capLine sps[i].Line
                        let endLine = capLine sps[i].EndLine
                        let startColumn = capColumn sps[i].Column
                        let endColumn = capColumn sps[i].EndColumn

                        let offsetDelta = // delta from previous offset
                            if i > 0 then
                                offset - capOffset sps[i - 1].Offset
                            else
                                offset

                        if i < 1 || offsetDelta > 0 then
                            builder.WriteCompressedInteger offsetDelta

                            // Check for hidden-sequence-point-record
                            if
                                startLine = 0xfeefee
                                || endLine = 0xfeefee
                                || (startColumn = 0 && endColumn = 0)
                                || ((endLine - startLine) = 0 && (endColumn - startColumn) = 0)
                            then
                                // Hidden-sequence-point-record
                                builder.WriteCompressedInteger 0
                                builder.WriteCompressedInteger 0
                            else
                                // Non-hidden-sequence-point-record
                                let deltaLines = endLine - startLine // lines
                                builder.WriteCompressedInteger deltaLines

                                let deltaColumns = endColumn - startColumn // Columns

                                if deltaLines = 0 then
                                    builder.WriteCompressedInteger deltaColumns
                                else
                                    builder.WriteCompressedSignedInteger deltaColumns

                                if previousNonHiddenStartLine < 0 then // delta Start Line & Column:
                                    builder.WriteCompressedInteger startLine
                                    builder.WriteCompressedInteger startColumn
                                else
                                    builder.WriteCompressedSignedInteger(startLine - previousNonHiddenStartLine)
                                    builder.WriteCompressedSignedInteger(startColumn - previousNonHiddenStartColumn)

                                previousNonHiddenStartLine <- startLine
                                previousNonHiddenStartColumn <- startColumn

                getDocumentHandle singleDocumentIndex, metadata.GetOrAddBlob builder

        metadata.AddMethodDebugInformation(docHandle, sequencePointBlob) |> ignore

        match minfo.RootScope with
        | None -> ()
        | Some scope -> writeMethodScopes minfo.MethToken scope

    member _.Emit() =
        sortMethods info
        metadata.SetCapacity(TableIndex.MethodDebugInformation, info.Methods.Length)

        defineModuleImportScope ()

        for minfo in info.Methods do
            emitMethod minfo

        let entryPoint =
            match info.EntryPoint with
            | None -> MetadataTokens.MethodDefinitionHandle 0
            | Some x -> MetadataTokens.MethodDefinitionHandle x

        // Compute the contentId for the pdb. Always do it deterministically, since we have to compute the anyway.
        // The contentId is the hash of the ID using whichever algorithm has been specified to the compiler
        let mutable contentHash = Array.empty<byte>

        let algorithmName, hashAlgorithm =
            match checksumAlgorithm with
            | HashAlgorithm.Sha1 -> "SHA1", SHA1.Create() :> System.Security.Cryptography.HashAlgorithm
            | HashAlgorithm.Sha256 -> "SHA256", SHA256.Create() :> System.Security.Cryptography.HashAlgorithm

        let idProvider =
            let convert (content: IEnumerable<Blob>) =
                let contentBytes = content |> Seq.collect (fun c -> c.GetBytes()) |> Array.ofSeq
                contentHash <- contentBytes |> hashAlgorithm.ComputeHash
                BlobContentId.FromHash contentHash

            Func<IEnumerable<Blob>, BlobContentId>(convert)

        let externalRowCounts = getRowCounts info.TableRowCounts

        let serializer =
            PortablePdbBuilder(metadata, externalRowCounts, entryPoint, idProvider)

        let blobBuilder = BlobBuilder()
        let contentId = serializer.Serialize blobBuilder
        let portablePdbStream = new MemoryStream()
        blobBuilder.WriteContentTo portablePdbStream
        reportTime "PDB: Created"
        (portablePdbStream.Length, contentId, portablePdbStream, algorithmName, contentHash)

let generatePortablePdb
    (embedAllSource: bool)
    (embedSourceList: string list)
    (sourceLink: string)
    checksumAlgorithm
    (info: PdbData)
    (pathMap: PathMap)
    =
    let generator =
        PortablePdbGenerator(embedAllSource, embedSourceList, sourceLink, checksumAlgorithm, info, pathMap)

    generator.Emit()

let compressPortablePdbStream (stream: MemoryStream) =
    let compressedStream = new MemoryStream()

    use compressionStream =
        new DeflateStream(compressedStream, CompressionMode.Compress, true)

    stream.WriteTo compressionStream
    compressedStream

let getInfoForPortablePdb
    (contentId: BlobContentId)
    pdbfile
    pathMap
    cvChunk
    deterministicPdbChunk
    checksumPdbChunk
    algorithmName
    checksum
    embeddedPdb
    deterministic
    =
    pdbGetDebugInfo
        (contentId.Guid.ToByteArray())
        (int32 contentId.Stamp)
        (PathMap.apply pathMap pdbfile)
        cvChunk
        None
        deterministicPdbChunk
        checksumPdbChunk
        algorithmName
        checksum
        0L
        None
        embeddedPdb
        deterministic

let getInfoForEmbeddedPortablePdb
    (uncompressedLength: int64)
    (contentId: BlobContentId)
    (compressedStream: MemoryStream)
    (pdbfile: string)
    cvChunk
    pdbChunk
    deterministicPdbChunk
    checksumPdbChunk
    algorithmName
    checksum
    deterministic
    =
    let fn = Path.GetFileName pdbfile

    pdbGetDebugInfo
        (contentId.Guid.ToByteArray())
        (int32 contentId.Stamp)
        !!fn
        cvChunk
        (Some pdbChunk)
        deterministicPdbChunk
        checksumPdbChunk
        algorithmName
        checksum
        uncompressedLength
        (Some compressedStream)
        true
        deterministic

//---------------------------------------------------------------------
// Dumps debug info into a text file for testing purposes
//---------------------------------------------------------------------
open Printf

let logDebugInfo (outfile: string) (info: PdbData) =
    use sw = new StreamWriter(new FileStream(outfile + ".debuginfo", FileMode.Create))

    fprintfn sw "ENTRYPOINT\r\n  %b\r\n" info.EntryPoint.IsSome
    fprintfn sw "DOCUMENTS"

    for i, doc in Seq.zip [ 0 .. info.Documents.Length - 1 ] info.Documents do
        // File names elided because they are ephemeral during testing
        fprintfn sw " [%d] <elided-for-testing>" i // doc.File
        fprintfn sw "     Type: %A" doc.DocumentType
        fprintfn sw "     Language: %A" doc.Language
        fprintfn sw "     Vendor: %A" doc.Vendor

    // Sort methods (because they are sorted in PDBs/MDBs too)
    fprintfn sw "\r\nMETHODS"
    Array.sortInPlaceBy (fun x -> x.MethToken) info.Methods

    for meth in info.Methods do
        fprintfn sw " %s" meth.MethName
        fprintfn sw "     Params: %A" [ for p in meth.Params -> sprintf "%d: %s" p.Index p.Name ]

        fprintfn
            sw
            "     Range: %A"
            (meth.DebugRange
             |> Option.map (fun (f, t) -> sprintf "[%d,%d:%d] - [%d,%d:%d]" f.Document f.Line f.Column t.Document t.Line t.Column))

        fprintfn sw "     Points:"

        for sp in meth.DebugPoints do
            fprintfn sw "      - Doc: %d Offset:%d [%d:%d]-[%d-%d]" sp.Document sp.Offset sp.Line sp.Column sp.EndLine sp.EndColumn

        // Walk through the tree of scopes and write all variables
        fprintfn sw "     Scopes:"

        let rec writeScope offs (scope: PdbMethodScope) =
            fprintfn sw "      %s- [%d-%d]" offs scope.StartOffset scope.EndOffset

            if scope.Locals.Length > 0 then
                fprintfn sw "      %s  Locals: %A" offs [ for p in scope.Locals -> sprintf "%d: %s" p.Index p.Name ]

            for child in scope.Children do
                writeScope (offs + "  ") child

        match meth.RootScope with
        | None -> ()
        | Some rootscope -> writeScope "" rootscope

        fprintfn sw ""

let rec allNamesOfScope acc (scope: PdbMethodScope) =
    let acc = (acc, scope.Locals) ||> Array.fold (fun z l -> Set.add l.Name z)
    let acc = (acc, scope.Children) ||> allNamesOfScopes
    acc

and allNamesOfScopes acc (scopes: PdbMethodScope[]) =
    (acc, scopes) ||> Array.fold allNamesOfScope

let rec pushShadowedLocals (stackGuard: StackGuard) (localsToPush: PdbLocalVar[]) (scope: PdbMethodScope) =
    stackGuard.Guard(fun () ->
        // Check if child scopes are properly nested
        if
            scope.Children
            |> Array.forall (fun child -> child.StartOffset >= scope.StartOffset && child.EndOffset <= scope.EndOffset)
        then

            let children = scope.Children |> Array.sortWith scopeSorter

            // Find all the names defined in this scope
            let scopeNames = set [| for n in scope.Locals -> n.Name |]

            // Rename if necessary as we push
            let rename, unprocessed =
                localsToPush |> Array.partition (fun l -> scopeNames.Contains l.Name)

            let renamed = [| for l in rename -> { l with Name = l.Name + " (shadowed)" } |]

            let localsToPush2 = [| yield! renamed; yield! unprocessed; yield! scope.Locals |]

            let newChildren, splits =
                children
                |> Array.map (pushShadowedLocals stackGuard localsToPush2)
                |> Array.unzip

            // Check if a rename in any of the children forces a split
            if splits |> Array.exists id then
                let results =
                    [|
                        // First fill in the gaps between the children with an adjusted version of this scope.
                        let gaps =
                            [|
                                yield (scope.StartOffset, scope.StartOffset)
                                for newChild in children do
                                    yield (newChild.StartOffset, newChild.EndOffset)
                                yield (scope.EndOffset, scope.EndOffset)
                            |]

                        for ((_, a), (b, _)) in Array.pairwise gaps do
                            if a < b then
                                yield
                                    { scope with
                                        Locals = localsToPush2
                                        Children = [||]
                                        StartOffset = a
                                        EndOffset = b
                                    }

                        yield! Array.concat newChildren
                    |]

                let results2 = results |> Array.sortWith scopeSorter
                results2, true
            else
                let splitsParent = renamed.Length > 0
                [| { scope with Locals = localsToPush2 } |], splitsParent
        else
            [| scope |], false)

// Check to see if a scope has a local with the same name as any of its children
//
// If so, do not emit 'scope' itself. Instead,
//  1. Emit a copy of 'scope' in each true gap, with all locals
//  2. Adjust each child scope to also contain the locals from 'scope',
//     adding the text " (shadowed)" to the names of those with name conflicts.
let unshadowScopes rootScope =
    // Avoid stack overflow when writing linearly nested scopes
    let UnshadowScopesStackGuardDepth =
        GetEnvInteger "FSHARP_ILPdb_UnshadowScopes_StackGuardDepth" 100

    let stackGuard =
        StackGuard(UnshadowScopesStackGuardDepth, "ILPdbWriter.unshadowScopes")

    let result, _ = pushShadowedLocals stackGuard [||] rootScope
    result
