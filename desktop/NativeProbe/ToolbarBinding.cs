using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using Microsoft.Diagnostics.Runtime;

namespace Popcorn;

internal sealed class ToolbarBinding : IDisposable
{
    private const string DacSha = "6F7A3520FB5A30D1C747E7D232B219C1C97A2270429DA7AA1F572AC2C60B28BE";
    private const int MaxDiscoveryHandles = 250_000;
    private readonly DataTarget target;
    private readonly ulong slot, pageType, handlerType, vmType;
    private readonly ulong toHandlers, toPage, toVm, hidden, media, pageIdentity;
    private readonly Guid initialPage;
    private ToolbarBinding(DataTarget target, ToolbarMetadata layout, ulong slot, ClrObject page, ClrObject handlers, ClrObject vm)
    {
        this.target = target;
        this.slot = slot;
        pageType = page.Type!.MethodTable;
        handlerType = handlers.Type!.MethodTable;
        vmType = vm.Type!.MethodTable;
        toHandlers = Offset(page, layout.PageHandlers);
        toPage = Offset(handlers, layout.HandlerPage);
        toVm = Offset(handlers, layout.HandlerViewModel);
        hidden = Offset(vm, layout.Hidden);
        media = Offset(vm, layout.Media);
        pageIdentity = Offset(vm, layout.PageIdentity);
        initialPage = ReadIdentity(vm.Address);
        if (initialPage == Guid.Empty) throw new InvalidDataException("page-identity-unavailable");
    }

    private static ulong Offset(ClrObject obj, string name)
    {
        ulong offset = checked((obj.Type!.GetFieldByName(name) ?? throw new InvalidDataException("field-unavailable")).GetAddress(obj.Address) - obj.Address);
        if (offset is < 8 or > 16384) throw new InvalidDataException("field-outside-bounded-layout");
        return offset;
    }

    internal static ToolbarBinding Open(int pid, string dacPath, CancellationToken token)
    {
        using var owner = Process.GetProcessById(pid);
        if (!owner.ProcessName.Equals("BaoMiHua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("not-baomihua");
        var creation = owner.StartTime;
        string directory = Path.GetDirectoryName(owner.MainModule!.FileName) ?? throw new InvalidDataException("unknown-player-path");
        string dll = Path.Combine(directory, "BaoMiHua.dll");
        if (!ToolbarLayoutProof.Matches(Path.Combine(directory,"resources.pri")))
            throw new InvalidDataException("unknown-toolbar-layout");
        var layout = ToolbarMetadata.Inspect(dll);
        using var dac = File.OpenRead(dacPath);
        if (!Convert.ToHexString(SHA256.HashData(dac)).Equals(DacSha, StringComparison.Ordinal)) throw new InvalidDataException("dac-hash-mismatch");

        token.ThrowIfCancellationRequested();
        var target = DataTarget.AttachToProcess(pid, suspend: false);
        try
        {
            using (var after = Process.GetProcessById(pid))
                if (after.StartTime != creation) throw new InvalidDataException("process-identity-changed");
            var clr = target.ClrVersions.Single();
            var version = FileVersionInfo.GetVersionInfo(dacPath);
            if (clr.Version != new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart))
                throw new InvalidDataException("clr-build-does-not-match-pinned-dac");
            var runtime = clr.CreateRuntime(dacPath);
            var module = runtime.EnumerateModules().Single(m => StringComparer.OrdinalIgnoreCase.Equals(m.Name, dll));
            if (module.MetadataLength is < 1 or > 8 * 1024 * 1024) throw new InvalidDataException("metadata-size");
            var bytes = new byte[(int)module.MetadataLength];
            if (target.DataReader.Read(module.MetadataAddress, bytes) != bytes.Length) throw new InvalidDataException("metadata-read");
            using var provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange(bytes));
            var reader = provider.GetMetadataReader();
            if (reader.GetGuid(reader.GetModuleDefinition().Mvid) != layout.Mvid) throw new InvalidDataException("mvid-mismatch");
            var pageType = module.GetTypeByName(ToolbarMetadata.Page)?.MethodTable ?? throw new InvalidDataException("page-type-unavailable");
            var handlerType = module.GetTypeByName(ToolbarMetadata.Handlers)?.MethodTable ?? throw new InvalidDataException("handler-type-unavailable");
            var vmType = module.GetTypeByName(ToolbarMetadata.ViewModel) ?? throw new InvalidDataException("model-type-unavailable");
            if (vmType.MetadataToken != layout.TypeToken || vmType.GetFieldByName(layout.Hidden)?.Token != layout.HiddenToken)
                throw new InvalidDataException("live-layout-mismatch");
            var pages = new Dictionary<ulong, (ClrHandle Handle, ClrObject Handlers, ClrObject Vm)>();
            int handles = 0;
            foreach (var handle in runtime.EnumerateHandles())
            {
                token.ThrowIfCancellationRequested();
                if (++handles > MaxDiscoveryHandles) throw new InvalidDataException("handle-budget-exhausted");
                if (handle.HandleKind is not (ClrHandleKind.RefCounted or ClrHandleKind.Strong) || handle.Object.Type?.MethodTable != pageType) continue;
                var page = handle.Object;
                var handler = page.ReadObjectField(layout.PageHandlers);
                if (handler.Type?.MethodTable != handlerType || handler.ReadObjectField(layout.HandlerPage).Address != page.Address) continue;
                var vm = handler.ReadObjectField(layout.HandlerViewModel);
                if (vm.Type?.MethodTable != vmType.MethodTable || vm.ReadObjectField(layout.Media).IsNull) continue;
                if (!pages.TryGetValue(page.Address, out var item) || item.Handle.HandleKind != ClrHandleKind.RefCounted)
                    pages[page.Address] = (handle, handler, vm);
                if (pages.Count > 1) throw new InvalidDataException("multiple-loaded-player-pages");
            }
            token.ThrowIfCancellationRequested();
            if (pages.Count != 1) throw new InvalidDataException("no-unique-rooted-player-page");
            var chosen = pages.Values.Single();
            var binding = new ToolbarBinding(target, layout, chosen.Handle.Address, chosen.Handle.Object, chosen.Handlers, chosen.Vm);
            if (binding.Read() == null) throw new InvalidDataException("initial-read-unstable");
            return binding;
        }
        catch { target.Dispose(); throw; }
    }

    internal bool? Read()
    {
        var first = ReadSample();
        if (first == null || first != ReadSample()) return null;
        return first.Value.Hidden;
    }

    private (ulong Page, ulong Handlers, ulong Vm, ulong Media, bool Hidden)? ReadSample()
    {
        // The managed objects can move; resolve them from the current handle slot on every read.
        ulong page = Ptr(slot);
        if (page == 0 || Ptr(page) != pageType) return null;
        ulong handlers = Ptr(page + toHandlers);
        if (handlers == 0 || Ptr(handlers) != handlerType || Ptr(handlers + toPage) != page) return null;
        ulong vm = Ptr(handlers + toVm);
        if (vm == 0 || Ptr(vm) != vmType || ReadIdentity(vm) != initialPage) return null;
        ulong mediaObject = Ptr(vm + media);
        Span<byte> value = stackalloc byte[1];
        if (mediaObject == 0 || target.DataReader.Read(vm + hidden, value) != 1 || value[0] > 1) return null;
        return (page, handlers, vm, mediaObject, value[0] == 1);
    }

    private Guid ReadIdentity(ulong address)
    {
        Span<byte> bytes = stackalloc byte[16];
        return target.DataReader.Read(address + pageIdentity, bytes) == bytes.Length ? new Guid(bytes) : Guid.Empty;
    }

    private ulong Ptr(ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        return target.DataReader.Read(address, bytes) == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
    }

    public void Dispose() => target.Dispose();
}
