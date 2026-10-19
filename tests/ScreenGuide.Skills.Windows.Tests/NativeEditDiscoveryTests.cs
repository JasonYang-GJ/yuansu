using Accessibility;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class NativeEditDiscoveryTests
{
    [Fact]
    public void LargeNativeTreeReadsIdentityOwnershipAndRoleWithoutAttributesOrState()
    {
        var nodes = Enumerable.Range(0, 1100).Select(index => new NativeAccessible(index + 1,
            index == 1099 ? AccessibleRole.Text : AccessibleRole.Client)).ToArray();
        var ownerReads = 0;
        var identity = LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, index =>
        {
            var node = LegacyBrowserSearchSemantics.ReadEditIdentityRole(nodes[index], 42, provider =>
            {
                ownerReads++;
                Assert.Equal(new IntPtr(420), provider);
                return 42;
            });
            return node is null ? null : new LegacyBrowserSearchSemantics.EditTreeNode<int>(node.Identity, node.Role,
                index == 0 ? Enumerable.Range(1, 100).ToArray()
                : index <= 9 ? Enumerable.Range(101 + (index - 1) * 111, 111).ToArray() : []);
        });

        Assert.Equal("ia2:420:1100", identity);
        Assert.Equal(1100, ownerReads);
        Assert.Equal(0, nodes.Sum(node => node.AttributeReads));
        Assert.Equal(0, nodes.Sum(node => node.StateReads));
        Assert.All(nodes, node =>
        {
            Assert.Equal(1, node.ServiceReads);
            Assert.Equal(1, node.IdentityReads);
            Assert.Equal(1, node.WindowReads);
            Assert.Equal(1, node.RoleReads);
        });
    }

    [Theory]
    [InlineData("query-failure")]
    [InlineData("missing-metadata")]
    [InlineData("identity-failure")]
    [InlineData("zero-identity")]
    [InlineData("window-failure")]
    [InlineData("foreign-owner")]
    [InlineData("role-unavailable")]
    public void MinimalProjectionStillRequiresEveryNodeToBeReadableAndBound(string scenario)
    {
        var node = new NativeAccessible(scenario == "zero-identity" ? 0 : 1, AccessibleRole.Text)
        { Failure = scenario };
        Assert.Null(LegacyBrowserSearchSemantics.ReadEditIdentityRole(node, 42,
            _ => scenario == "foreign-owner" ? 43 : 42));
    }

    [Theory]
    [InlineData(AccessibleStates.Unavailable)]
    [InlineData(AccessibleStates.Invisible)]
    [InlineData(AccessibleStates.Protected)]
    public void HiddenDisabledOrPasswordEditStillMakesNativeTreeAmbiguous(AccessibleStates state)
    {
        var nodes = new[]
        {
            new NativeAccessible(1, AccessibleRole.Client),
            new NativeAccessible(2, AccessibleRole.Text),
            new NativeAccessible(3, AccessibleRole.Text) { TestState = state }
        };
        var identity = LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, index =>
        {
            var node = LegacyBrowserSearchSemantics.ReadEditIdentityRole(nodes[index], 42, _ => 42);
            return node is null ? null : new LegacyBrowserSearchSemantics.EditTreeNode<int>(node.Identity,
                node.Role, index == 0 ? [1, 2] : []);
        });

        Assert.Null(identity);
        Assert.All(nodes, node => Assert.Equal(0, node.StateReads));
    }

    [Fact]
    public void CompleteNativeTreeAllowsExactlyItsOnlyEditToUseFastUiaLookup()
    {
        var reads = new List<int>();
        var identity = LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, node =>
        {
            reads.Add(node);
            return new(node.ToString(), node == 3 ? 42 : 10, node == 0 ? [1, 2, 3] : []);
        });
        var control = Control("3");
        Assert.Same(control, WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(() => identity, () => control, 42));
        Assert.Equal(new[] { 0, 1, 2, 3 }, reads);
    }

    [Theory]
    [InlineData("second-input")]
    [InlineData("unreadable-node")]
    [InlineData("partial-child-list")]
    [InlineData("missing-id")]
    [InlineData("cycle")]
    public void IncompleteOrAmbiguousNativeEvidenceCannotSkipFullUiaDiscovery(string scenario)
    {
        var identity = LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, node =>
        {
            if (node == 2 && scenario == "unreadable-node") return null;
            return new LegacyBrowserSearchSemantics.EditTreeNode<int>(
                node == 2 && scenario == "missing-id" ? "" : node.ToString(),
                node == 1 || node == 2 && scenario == "second-input" ? 42 : 10,
                node == 0 ? [1, 2] : node == 2 && scenario == "cycle" ? [0]
                    : node == 2 && scenario == "partial-child-list" ? Enumerable.Range(3, 129).ToArray() : []);
        });
        Assert.Null(identity);
        var firstLookups = 0;
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(() => identity,
            () => { firstLookups++; return Control("1"); }, 42));
        Assert.Equal(0, firstLookups);
    }

    [Fact]
    public void TraversalLimitsCannotProduceAFalseUniquenessProof()
    {
        Assert.Null(LegacyBrowserSearchSemantics.FindSoleEditIdentity(0,
            node => new(node.ToString(), node == 1 ? 42 : 10, [node + 1])));
        var visited = 0;
        Assert.Null(LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, node =>
        {
            visited++;
            return new(node.ToString(), node == 1 ? 42 : 10,
                node < 100 ? Enumerable.Range(node * 100 + 1, 100).ToArray() : []);
        }));
        Assert.Equal(4096, visited);
    }

    [Theory]
    [InlineData("changed-native-id")]
    [InlineData("changed-window")]
    [InlineData("disappeared")]
    [InlineData("exception")]
    public void FirstUiaEditMustMatchCurrentBoundNativeIdentity(string scenario)
    {
        var control = Control(scenario == "changed-native-id" ? "other" : "input");
        if (scenario == "changed-window") control = control with { Snapshot = control.Snapshot with { OwnerWindowHandle = 99 } };
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(() => "input", () => scenario switch
        {
            "disappeared" => null,
            "exception" => throw new System.Windows.Automation.ElementNotAvailableException(),
            _ => control
        }, 42));
    }

    [Fact]
    public void FastDiscoveryDoesNotTurnNativeIdentityIntoSearchAuthority()
    {
        var password = Control("input");
        password = password with { Snapshot = password.Snapshot with { IsPassword = true } };
        var discovered = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(() => "input", () => password, 42);
        Assert.NotNull(discovered);
        Assert.False(SafeForegroundSearchExecutor.IsReliableCandidate(discovered.Snapshot, 42));
    }

    [Fact]
    public void PreparationCanPrimeUiaBeforeRequiringCompleteNativeUniqueness()
    {
        var calls = new List<string>();
        var nativeTreeAvailable = false;
        var control = Control("input");
        var discovered = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(
            () =>
            {
                calls.Add("native");
                // A cold provider may expose its complete native tree only after a UIA query.
                return nativeTreeAvailable ? "input" : null;
            },
            () =>
            {
                calls.Add("uia");
                nativeTreeAvailable = true;
                return control;
            }, 42, primeUiaFirst: true);

        Assert.Same(control, discovered);
        Assert.Equal(new[] { "uia", "native" }, calls);
    }

    [Theory]
    [InlineData("second-input")]
    [InlineData("unreadable-node")]
    [InlineData("partial-child-list")]
    [InlineData("missing-id")]
    [InlineData("cycle")]
    public void PrimedUiaCandidateStillRequiresCompleteUnambiguousNativeEvidence(string scenario)
    {
        var firstLookups = 0;
        var nativeReads = 0;
        var discovered = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(
            () =>
            {
                nativeReads++;
                return LegacyBrowserSearchSemantics.FindSoleEditIdentity(0, node =>
                {
                    if (node == 2 && scenario == "unreadable-node") return null;
                    return new LegacyBrowserSearchSemantics.EditTreeNode<int>(
                        node == 2 && scenario == "missing-id" ? "" : node.ToString(),
                        node == 1 || node == 2 && scenario == "second-input" ? 42 : 10,
                        node == 0 ? [1, 2] : node == 2 && scenario == "cycle" ? [0]
                            : node == 2 && scenario == "partial-child-list" ? Enumerable.Range(3, 129).ToArray() : []);
                });
            },
            () => { firstLookups++; return Control("1"); }, 42, primeUiaFirst: true);

        Assert.Null(discovered);
        Assert.Equal(1, firstLookups);
        Assert.Equal(1, nativeReads);
    }

    [Theory]
    [InlineData("changed-native-id")]
    [InlineData("changed-window")]
    [InlineData("missing-native-id")]
    [InlineData("disappeared")]
    public void PrimedUiaCandidateMustMatchTheSubsequentNativeProof(string scenario)
    {
        var control = Control(scenario == "missing-native-id" ? "" : "input");
        if (scenario == "changed-window")
            control = control with { Snapshot = control.Snapshot with { OwnerWindowHandle = 99 } };
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(
            () => scenario == "changed-native-id" ? "replacement" : "input",
            () => scenario == "disappeared" ? null : control, 42, primeUiaFirst: true));
    }

    [Theory]
    [InlineData("uia", false)]
    [InlineData("uia", true)]
    [InlineData("native", false)]
    [InlineData("native", true)]
    public void PrimedPreparationRejectsUnavailableReadsAtEitherBoundary(string boundary, bool comFailure)
    {
        Exception Failure() => comFailure ? new COMException("Unavailable test provider")
            : new System.Windows.Automation.ElementNotAvailableException();
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(
            () => boundary == "native" ? throw Failure() : "input",
            () => boundary == "uia" ? throw Failure() : Control("input"), 42, primeUiaFirst: true));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("read-only")]
    [InlineData("no-value-pattern")]
    public void PrimingAndNativeUniquenessDoNotGrantInputAuthority(string unsafeState)
    {
        var control = Control("input");
        control = control with
        {
            Snapshot = control.Snapshot with
            {
                IsPassword = unsafeState == "password",
                IsEnabled = unsafeState != "disabled",
                IsOffscreen = unsafeState == "offscreen",
                IsReadOnly = unsafeState == "read-only",
                SupportsValuePattern = unsafeState != "no-value-pattern"
            }
        };
        var discovered = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEdit(
            () => "input", () => control, 42, primeUiaFirst: true);

        Assert.NotNull(discovered);
        Assert.False(SafeForegroundSearchExecutor.IsReliableCandidate(discovered.Snapshot, 42));
    }

    private static DesktopSearchControl Control(string identity) => new(identity,
        new DesktopSearchControlSnapshot("uia:" + identity, 42, "搜索", "", "", "", true, false,
            false, false, true, false, new(10, 10, 200, 30), "", identity, true));

    // Managed implementations exercise the production COM projection without a window,
    // UI Automation session, or application process. Only ownership resolution is supplied.
    private sealed class NativeAccessible(int id, AccessibleRole role) : AccessibleObject,
        LegacyBrowserSearchSemantics.IAccessibleServices, LegacyBrowserSearchSemantics.IAccessible2Metadata
    {
        public string Failure { get; init; } = "";
        public AccessibleStates TestState { get; init; }
        public int ServiceReads { get; private set; }
        public int IdentityReads { get; private set; }
        public int WindowReads { get; private set; }
        public int RoleReads { get; private set; }
        public int AttributeReads { get; private set; }
        public int StateReads { get; private set; }
        public override AccessibleRole Role
        {
            get
            {
                RoleReads++;
                if (Failure == "role-unavailable") throw new COMException("Unavailable test node");
                return role;
            }
        }
        public override AccessibleStates State { get { StateReads++; return TestState; } }

        public int QueryService(ref Guid service, ref Guid iid, out object result)
        {
            ServiceReads++;
            Assert.Equal(typeof(IAccessible).GUID, service);
            Assert.Equal(typeof(LegacyBrowserSearchSemantics.IAccessible2Metadata).GUID, iid);
            result = Failure == "missing-metadata" ? new object() : this;
            return Failure == "query-failure" ? -1 : 0;
        }
        public int UniqueId(out int value)
        {
            IdentityReads++;
            value = id;
            return Failure == "identity-failure" ? -1 : 0;
        }
        public int WindowHandle(out IntPtr handle)
        {
            WindowReads++;
            handle = new IntPtr(420);
            return Failure == "window-failure" ? -1 : 0;
        }
        public int Attributes(out string attributes)
        {
            AttributeReads++;
            attributes = "tag:input;text-input-type:search;";
            return 0;
        }
        public void _VtblGap1_38() => throw new NotSupportedException();
        public void _VtblGap2_2() => throw new NotSupportedException();
    }
}
