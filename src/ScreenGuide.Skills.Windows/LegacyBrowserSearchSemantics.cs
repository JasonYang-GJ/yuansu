using Accessibility;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ScreenGuide.Skills.Windows;

/// <summary>
/// Local, window-bound MSAA/IA2 metadata bridge for Chromium/CEF search inputs.
/// Never uses desktop hit testing, screenshots, pointer movement or a debug/network endpoint.
/// ABI: Linux Foundation IAccessible2 1.3, inherited IAccessible vtable + uniqueID/windowHandle/attributes.
/// </summary>
internal static class LegacyBrowserSearchSemantics
{
    internal sealed record Root(long Handle, IAccessible Accessible);
    internal sealed record Evidence(string Identity, bool IsSearch, bool HasKeyboardFocus = false,
        bool UsesSearchContainer = false, bool SearchContainerPending = false);
    internal sealed record Node(IAccessible Accessible, string Identity, int Role, int State, IReadOnlyDictionary<string, string> Attributes);
    internal sealed record SearchContextPart(string Identity, long OwnerWindow, int Role, string Tag, string InputType, string Name, bool Available);
    internal sealed record EditTreeNode<T>(string Identity, int Role, IReadOnlyList<T> Children);
    internal sealed record EditIdentityRole(string Identity, int Role);
    internal enum NativeEditProofState { Unique, None, Multiple, Incomplete }
    internal sealed record NativeEditProof(NativeEditProofState State, string? Identity = null);

    private static EditIdentityRole? ReadEditIdentityRole(IAccessible accessible, long window)
        => ReadEditIdentityRole(accessible, window, static handle => GetAncestor(handle, 2).ToInt64());

    // Uniqueness needs identity, ownership and role only. Do not read attributes/state on
    // every document node; the selected UIA input still receives full semantic/safety checks.
    // The resolver is call-local for offline tests; the production overload always uses Win32.
    internal static EditIdentityRole? ReadEditIdentityRole(IAccessible accessible, long window, Func<IntPtr, long> readOwnerWindow)
    {
        try
        {
            if (accessible is not IAccessibleServices services) return null;
            var service = typeof(IAccessible).GUID; var iid = typeof(IAccessible2Metadata).GUID;
            if (services.QueryService(ref service, ref iid, out var result) != 0 || result is not IAccessible2Metadata metadata
                || metadata.UniqueId(out var id) != 0 || id == 0 || metadata.WindowHandle(out var provider) != 0
                || readOwnerWindow(provider) != window) return null;
            return new($"ia2:{provider.ToInt64()}:{id}", Convert.ToInt32(accessible.get_accRole(0)));
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or OverflowException) { return null; }
    }

    internal static string? FindSoleEditIdentity<T>(T root, Func<T, EditTreeNode<T>?> read)
        => FindNativeEditProof(root, read) is { State: NativeEditProofState.Unique } proof ? proof.Identity : null;

    internal static NativeEditProof FindNativeEditProof<T>(T root, Func<T, EditTreeNode<T>?> read)
    {
        var queue = new Queue<(T Node, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? editIdentity = null;
        while (queue.Count > 0)
        {
            if (visited.Count >= 4096) return new(NativeEditProofState.Incomplete);
            var (accessible, depth) = queue.Dequeue();
            var node = read(accessible);
            if (node is null || string.IsNullOrEmpty(node.Identity) || !visited.Add(node.Identity)
                || node.Children.Count > 128 || (depth >= 64 && node.Children.Count > 0)) return new(NativeEditProofState.Incomplete);
            if (node.Role == 42)
            {
                if (editIdentity is not null) return new(NativeEditProofState.Multiple);
                editIdentity = node.Identity;
            }
            foreach (var child in node.Children) queue.Enqueue((child, depth + 1));
        }
        return editIdentity is null ? new(NativeEditProofState.None) : new(NativeEditProofState.Unique, editIdentity);
    }

    internal static string? ReadSoleEditIdentity(Root root, long window)
        => ReadNativeEditProof(root, window) is { State: NativeEditProofState.Unique } proof ? proof.Identity : null;

    internal static NativeEditProof ReadNativeEditProof(Root root, long window)
        => ReadBoundNativeEditProof(
            () => GetAncestor(new IntPtr(root.Handle), 2).ToInt64() == window,
            () => ReadEditIdentityRole(root.Accessible, window)?.Identity,
            // A new traversal owns its queue, visited set and candidate identity. No partial
            // tree or identity from an earlier incomplete attempt can establish uniqueness.
            () => FindNativeEditProof(root.Accessible, accessible =>
            {
                var node = ReadEditIdentityRole(accessible, window);
                return node is not null && TryReadCompleteSearchChildren(accessible, out var children)
                    ? new EditTreeNode<IAccessible>(node.Identity, node.Role, children) : null;
            }));

    internal static NativeEditProof ReadBoundNativeEditProof(Func<bool> isOwnedByWindow,
        Func<string?> readRootIdentity, Func<NativeEditProof> readCompleteTree)
    {
        try
        {
            if (!isOwnedByWindow()) return new(NativeEditProofState.Incomplete);
            var rootIdentity = readRootIdentity();
            if (string.IsNullOrEmpty(rootIdentity)) return new(NativeEditProofState.Incomplete);
            // Enumerate the complete native tree, including hidden/non-search inputs. The UIA
            // legacy proxy repeats expensive per-node translation during FindAll; MSAA provides
            // a complete child batch while retaining each node's independently bound IA2 identity.
            var proof = readCompleteTree();
            if (proof.State != NativeEditProofState.Unique) return proof;
            return isOwnedByWindow() && readRootIdentity() == rootIdentity
                ? proof : new(NativeEditProofState.Incomplete);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or OverflowException)
        { return new(NativeEditProofState.Incomplete); }
    }

    internal static Node? ReadNode(IAccessible accessible, long window)
    {
        try
        {
            if (accessible is not IAccessibleServices services) return null;
            var service = typeof(IAccessible).GUID; var iid = typeof(IAccessible2Metadata).GUID;
            if (services.QueryService(ref service, ref iid, out var result) != 0 || result is not IAccessible2Metadata metadata
                || metadata.UniqueId(out var id) != 0 || id == 0 || metadata.WindowHandle(out var provider) != 0
                || GetAncestor(provider, 2).ToInt64() != window || metadata.Attributes(out var attributes) != 0
                || ParseAttributes(attributes) is not { } parsed) return null;
            return new(accessible, $"ia2:{provider.ToInt64()}:{id}", Convert.ToInt32(accessible.get_accRole(0)),
                Convert.ToInt32(accessible.get_accState(0)), parsed);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or OverflowException) { return null; }
    }

    internal static IReadOnlyList<IAccessible> Children(IAccessible accessible)
    {
        var count = accessible.accChildCount;
        if (count is < 0 or > 300) return [];
        var children = new object[count];
        if (count == 0 || AccessibleChildren(accessible, 0, count, children, out var obtained) < 0) return [];
        return children.Take(obtained).OfType<IAccessible>().ToArray();
    }

    internal static IReadOnlyList<Root> Open(long window)
    {
        var roots = new List<Root>();
        EnumWindows callback = (child, unusedState) =>
        {
            if (roots.Count >= 16) return false;
            var name = new StringBuilder(128); GetClassName(child, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND" || GetAncestor(child, 2).ToInt64() != window) return true;
            try
            {
                Guid iid = typeof(IAccessible).GUID;
                if (AccessibleObjectFromWindow(child, 0xfffffffc, ref iid, out var accessible) == 0 && accessible is not null)
                {
                    // A real accessibility client asks for the document role; no global accessibility setting is changed.
                    _ = accessible.get_accRole(0);
                    _ = accessible.accChildCount;
                    roots.Add(new(child.ToInt64(), accessible));
                }
            }
            catch (COMException) { }
            return true;
        };
        EnumChildWindows(new IntPtr(window), callback, IntPtr.Zero);
        return roots;
    }

    internal static Evidence? Read(AutomationElement element, long window, IReadOnlyList<Root> roots)
        => ReadBound(element, window, roots)?.Evidence;

    internal static bool TryFocus(AutomationElement element, long window, IReadOnlyList<Root> roots, string expectedIdentity)
    {
        var bound = ReadBound(element, window, roots);
        if (bound is null || !bound.Value.Evidence.IsSearch || bound.Value.Evidence.Identity != expectedIdentity) return false;
        try
        {
            // SELFLAG_TAKEFOCUS targets this exact native accessible input; it does not move
            // the pointer or synthesize a Tab sequence. The caller must verify focus before writing.
            bound.Value.Accessible.accSelect(1, 0);
            var focused = ReadBound(element, window, roots);
            return focused is not null && focused.Value.Evidence.Identity == expectedIdentity
                && focused.Value.Evidence.IsSearch && focused.Value.Evidence.HasKeyboardFocus;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static (IAccessible Accessible, Evidence Evidence)? ReadBound(AutomationElement element, long window, IReadOnlyList<Root> roots)
    {
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || element.Current.IsPassword) return null;
        if (!GetWindowRect(new IntPtr(window), out var windowBounds)
            || bounds.Left < windowBounds.Left || bounds.Top < windowBounds.Top
            || bounds.Right > windowBounds.Right || bounds.Bottom > windowBounds.Bottom) return null;
        foreach (var root in roots)
        {
            if (GetAncestor(new IntPtr(root.Handle), 2).ToInt64() != window) continue;
            try
            {
                // Hit test INSIDE the bound renderer's accessible tree, never the global desktop.
                if (root.Accessible.accHitTest((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2)) is not IAccessible native
                    || Convert.ToInt32(native.get_accRole(0)) != 42 || native is not IAccessibleServices services) continue;
                native.accLocation(out int x, out int y, out int width, out int height, 0);
                if (x != bounds.X || y != bounds.Y || width != bounds.Width || height != bounds.Height
                    || !string.Equals(native.get_accName(0) ?? "", element.Current.Name ?? "", StringComparison.Ordinal)) continue;
                var service = typeof(IAccessible).GUID; var iid = typeof(IAccessible2Metadata).GUID;
                if (services.QueryService(ref service, ref iid, out var result) != 0 || result is not IAccessible2Metadata metadata
                    || metadata.UniqueId(out int id) != 0 || id == 0
                    || metadata.WindowHandle(out var providerWindow) != 0
                    || GetAncestor(providerWindow, 2).ToInt64() != window
                    || metadata.Attributes(out var attributes) != 0) continue;
                var identity = $"ia2:{providerWindow.ToInt64()}:{id}";
                var nativeType = IsSearchInput(attributes);
                var textSearch = !nativeType && IsLabeledTextSearchInput(attributes, native.get_accName(0)?.Trim() ?? "");
                var inSearchContainer = textSearch && HasBoundSearchContainer(native, window, identity, attributes);
                return (native, new(identity, nativeType || inSearchContainer,
                    (Convert.ToInt32(native.get_accState(0)) & 4) != 0, textSearch, textSearch && !inSearchContainer));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ElementNotAvailableException or OverflowException) { }
        }
        return null;
    }

    internal static bool IsSearchInput(string? attributes)
    {
        var fields = ParseAttributes(attributes);
        return fields is not null && fields.GetValueOrDefault("tag") == "input"
            && fields.GetValueOrDefault("text-input-type") == "search";
    }

    private static bool HasBoundSearchContainer(IAccessible input, long window, string identity, string attributes)
    {
        var name = input.get_accName(0)?.Trim() ?? "";
        if (!IsLabeledTextSearchInput(attributes, name)) return false;
        var ancestor = input;
        for (var level = 0; level < 2; level++)
        {
            if (ancestor.accParent is not IAccessible parent || ReadNode(parent, window) is null) return false;
            ancestor = parent;
            if (parent.accChildCount is < 1 or > 4) continue;
            var queue = new Queue<(IAccessible Accessible, int Depth)>();
            queue.Enqueue((parent, 0));
            var parts = new List<SearchContextPart>();
            var complete = true;
            while (queue.Count > 0 && parts.Count < 128)
            {
                var (accessible, depth) = queue.Dequeue();
                var node = ReadNode(accessible, window);
                if (node is null) { complete = false; break; }
                parts.Add(new(node.Identity, window, node.Role, node.Attributes.GetValueOrDefault("tag") ?? "",
                    node.Attributes.GetValueOrDefault("text-input-type") ?? "",
                    node.Role is 42 or 43 ? accessible.get_accName(0)?.Trim() ?? "" : "",
                    (node.State & (0x1 | 0x8000 | 0x10000)) == 0));
                if (node.Role == 42)
                {
                    if (accessible.accChildCount != 0) complete = false;
                    continue;
                }
                if (depth >= 8)
                {
                    if (accessible.accChildCount > 0) complete = false;
                    continue;
                }
                if (!TryReadCompleteSearchChildren(accessible, out var children)) { complete = false; break; }
                foreach (var child in children) queue.Enqueue((child, depth + 1));
            }
            if (complete && queue.Count == 0 && IsBoundSearchContainer(parts, window, identity)) return true;
        }
        return false;
    }

    internal static bool TryReadCompleteSearchChildren(IAccessible accessible, out IReadOnlyList<IAccessible> children)
    {
        children = [];
        var count = accessible.accChildCount;
        if (count is < 0 or > 128) return false;
        if (count == 0) return accessible.accChildCount == 0;
        var raw = new object[count];
        var result = AccessibleChildren(accessible, 0, count, raw, out var obtained);
        if (!IsCompleteSearchEnumeration(count, obtained, accessible.accChildCount, result,
            raw.Select(child => child is IAccessible).ToArray())) return false;
        children = raw.Cast<IAccessible>().ToArray();
        return true;
    }

    internal static bool IsCompleteSearchEnumeration(int expected, int obtained, int after, int result, IReadOnlyList<bool> accessibleObjects) =>
        expected is >= 0 and <= 128 && result == 0 && expected == obtained && expected == after
        && accessibleObjects.Count == expected && accessibleObjects.All(value => value);

    internal static bool IsBoundSearchContainer(IReadOnlyList<SearchContextPart> parts, long window, string identity)
    {
        if (parts.Count is < 2 or > 128 || parts.Any(part => part.OwnerWindow != window)) return false;
        var inputs = parts.Where(part => part.Role == 42 || part.Tag is "input" or "textarea").ToArray();
        var buttons = parts.Where(part => part.Role == 43 || part.Tag == "button").ToArray();
        return inputs.Length == 1 && inputs[0].Available && inputs[0].Identity == identity && inputs[0].Role == 42
            && inputs[0].Tag == "input" && inputs[0].InputType == "text" && IsSearchInputName(inputs[0].Name)
            && buttons.Length == 1 && buttons[0].Available && buttons[0].Role == 43 && buttons[0].Tag == "button"
            && buttons[0].Name is "搜索" or "Search";
    }

    private static bool IsSearchInputName(string name) => name is "搜索" or "搜索框" or "搜索内容" or "搜索你感兴趣的内容"
        or "Search" or "Search content";

    internal static bool IsLabeledTextSearchInput(string attributes, string name)
    {
        var fields = ParseAttributes(attributes);
        return fields is not null && fields.GetValueOrDefault("tag") == "input"
            && fields.GetValueOrDefault("text-input-type") == "text"
            && fields.GetValueOrDefault("name-from") == "placeholder" && IsSearchInputName(name);
    }

    internal static IReadOnlyDictionary<string, string>? ParseAttributes(string? attributes)
    {
        if (attributes is null || attributes.Length > 16384) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var key = new StringBuilder(); var value = new StringBuilder();
        var inValue = false; var escaped = false;
        foreach (var ch in attributes)
        {
            var destination = inValue ? value : key;
            if (escaped) { destination.Append(ch); escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == ':' && !inValue) { inValue = true; continue; }
            if (ch != ';') { destination.Append(ch); continue; }
            if (!inValue || !fields.TryAdd(key.ToString(), value.ToString())) return null;
            key.Clear(); value.Clear(); inValue = false;
        }
        if (escaped || key.Length != 0 || value.Length != 0 || inValue) return null;
        return fields;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAccessibleServices
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAccessible2Metadata
    {
        void _VtblGap1_38(); // IDispatch, IAccessible, and the first 13 IAccessible2 members after IUnknown.
        [PreserveSig] int UniqueId(out int id);
        [PreserveSig] int WindowHandle(out IntPtr handle);
        void _VtblGap2_2(); // indexInParent, locale.
        [PreserveSig] int Attributes([MarshalAs(UnmanagedType.BStr)] out string attributes);
    }

    private delegate bool EnumWindows(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindows callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible container, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
}
