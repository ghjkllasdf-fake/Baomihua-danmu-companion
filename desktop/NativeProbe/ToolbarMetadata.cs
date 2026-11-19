using System;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Popcorn;

internal sealed record ToolbarMetadata(Guid Mvid, int TypeToken, int HiddenToken, string Hidden, string Media, string PageIdentity,
    string PageHandlers, string HandlerPage, string HandlerViewModel)
{
    internal const string ViewModel = "Filmly.ViewModels.PlayerPageViewModel";
    internal const string Page = "Filmly.Views.PlayerPage";
    internal const string Handlers = "Filmly.Views.PlayerPageEventHandlers";

    internal static ToolbarMetadata Inspect(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        var md = pe.GetMetadataReader();
        string Full(TypeDefinitionHandle h) { var t = md.GetTypeDefinition(h); return md.GetString(t.Namespace) + "." + md.GetString(t.Name); }
        var handle = md.TypeDefinitions.Single(h => Full(h) == ViewModel);
        var type = md.GetTypeDefinition(handle);
        FieldDefinitionHandle Backing(string property)
        {
            var p = type.GetProperties().Select(md.GetPropertyDefinition).Single(x => md.GetString(x.Name) == property);
            var getter = md.GetMethodDefinition(p.GetAccessors().Getter);
            var il = pe.GetMethodBody(getter.RelativeVirtualAddress).GetILBytes();
            if (il is not { Length: 7 } || il[0] != 0x02 || il[1] != 0x7b || il[6] != 0x2a)
                throw new InvalidDataException("getter-is-not-a-direct-field-read");
            var field = MetadataTokens.Handle(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(2)));
            if (field.Kind != HandleKind.FieldDefinition || !type.GetFields().Contains((FieldDefinitionHandle)field))
                throw new InvalidDataException("getter-field-not-on-expected-type");
            var definition = md.GetFieldDefinition((FieldDefinitionHandle)field);
            if ((definition.Attributes & FieldAttributes.Static) != 0) throw new InvalidDataException("unexpected-static-field");
            return (FieldDefinitionHandle)field;
        }
        var hidden = Backing("ControlsPanelHidden");
        var media = Backing("Media");
        var identity = Backing("ClassUniqueId");
        if (!md.GetBlobBytes(md.GetFieldDefinition(hidden).Signature).AsSpan().SequenceEqual(new byte[] { 6, 2 }))
            throw new InvalidDataException("hidden-field-is-not-a-boolean");
        bool FieldNamesType(FieldDefinition f, string name)
        {
            var sig = md.GetBlobReader(f.Signature);
            if (sig.ReadByte() != 6 || sig.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle) return false;
            var t = sig.ReadTypeHandle();
            return t.Kind == HandleKind.TypeDefinition && Full((TypeDefinitionHandle)t) == name;
        }
        string Link(string owner, string target)
        {
            var declaring = md.GetTypeDefinition(md.TypeDefinitions.Single(h => Full(h) == owner));
            var f = declaring.GetFields().Select(md.GetFieldDefinition).Single(f => (f.Attributes & FieldAttributes.Static) == 0 && FieldNamesType(f, target));
            return md.GetString(f.Name);
        }
        var identitySig = md.GetBlobReader(md.GetFieldDefinition(identity).Signature);
        if (identitySig.ReadByte() != 6 || identitySig.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
            throw new InvalidDataException("page-id-is-not-a-guid");
        var identityType = identitySig.ReadTypeHandle();
        if (identityType.Kind != HandleKind.TypeReference) throw new InvalidDataException("page-id-is-not-a-guid");
        var guid = md.GetTypeReference((TypeReferenceHandle)identityType);
        if (md.GetString(guid.Namespace) != "System" || md.GetString(guid.Name) != "Guid") throw new InvalidDataException("page-id-is-not-a-guid");
        return new(md.GetGuid(md.GetModuleDefinition().Mvid), MetadataTokens.GetToken(handle), MetadataTokens.GetToken(hidden),
            md.GetString(md.GetFieldDefinition(hidden).Name), md.GetString(md.GetFieldDefinition(media).Name), md.GetString(md.GetFieldDefinition(identity).Name),
            Link(Page, Handlers), Link(Handlers, Page), Link(Handlers, ViewModel));
    }
}
