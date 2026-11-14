using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcEngineeringValidatorTests
{
    private static DbcSignal Signal(string name, int start, int bits, int? mux = null) =>
        new(name, start, bits, DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECU",
            MultiplexValue: mux);

    [Fact]
    public void ReportsTrueBitOverlapAndDoesNotInventTraceEvidence()
    {
        var message = new DbcMessage(0x100, "M", 8, "ECU", false,
            [Signal("A", 0, 8), Signal("B", 4, 8)]);
        var issues = DbcEngineeringValidator.Validate(message);
        Assert.Contains(issues, issue => issue.Code == "BIT_OVERLAP" &&
            issue.Signal == "A" && issue.Detail.Contains("bit 4", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowsDisjointSimpleMultiplexBranchesToReuseBits()
    {
        var mux = Signal("Selector", 0, 4) with { IsMultiplexor = true };
        var message = new DbcMessage(0x100, "M", 8, "ECU", false,
            [mux, Signal("Mode0", 8, 8, 0), Signal("Mode1", 8, 8, 1)]);
        Assert.DoesNotContain(DbcEngineeringValidator.Validate(message), issue =>
            issue.Code is "BIT_OVERLAP" or "MUX_OVERLAP_REVIEW");
    }

    [Fact]
    public void NestedOrAmbiguousMultiplexingIsReviewNotDefiniteConflict()
    {
        var a = Signal("A", 8, 8, 0);
        var b = Signal("B", 8, 8, 1);
        var message = new DbcMessage(0x100, "M", 8, "ECU", false, [a, b]);
        Assert.Contains(DbcEngineeringValidator.Validate(message), issue =>
            issue.Code == "MUX_OVERLAP_REVIEW" &&
            issue.Severity == DbcEngineeringSeverity.Review);
    }

    [Fact]
    public void ReportsOutOfBoundsMotorolaAndImpossibleEnums()
    {
        var sig = Signal("S", 0, 8) with
        {
            ByteOrder = DbcByteOrder.Motorola,
            ValueDescriptions = new Dictionary<long, string> { [-1] = "Invalid unsigned code", [256] = "Too large" }
        };
        var message = new DbcMessage(0x100, "M", 1, "ECU", false, [sig]);
        var issues = DbcEngineeringValidator.Validate(message);
        Assert.Contains(issues, issue => issue.Code == "OUTSIDE_DLC");
        Assert.Equal(2, issues.Count(issue => issue.Code == "VAL_OUT_OF_RANGE"));
    }

    [Fact]
    public void RejectsInvalidStandardCanIdentifierAndZeroFactor()
    {
        var signal = Signal("S", 0, 8) with { Factor = 0 };
        var message = new DbcMessage(0x800, "M", 8, "ECU", false, [signal]);
        var issues = DbcEngineeringValidator.Validate(message);
        Assert.Contains(issues, issue => issue.Code == "CAN_ID_RANGE");
        Assert.Contains(issues, issue => issue.Code == "ZERO_FACTOR");
    }

    [Fact]
    public void RecognizesDisjointExtendedMultiplexRanges()
    {
        var a = Signal("A", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch", [new DbcMuxRange(0, 2)])]
        };
        var b = Signal("B", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch", [new DbcMuxRange(3, 5)])]
        };
        var selector = Signal("Switch", 0, 3) with { IsMultiplexor = true };
        var message = new DbcMessage(0x100, "M", 8, "ECU", false,
            [selector, a, b]);
        Assert.DoesNotContain(DbcEngineeringValidator.Validate(message), issue =>
            issue.Code is "BIT_OVERLAP" or "MUX_OVERLAP_REVIEW" or
                "MUX_CONTROLLER_MISSING" or "MUX_RANGE_OUT_OF_BOUNDS");
    }

    [Fact]
    public void MissingExtendedMuxControllerCannotSilentlySuppressAnOverlap()
    {
        var a = Signal("BranchA", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("UndeclaredSwitch",
                [new DbcMuxRange(0, 2)])]
        };
        var b = Signal("BranchB", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("UndeclaredSwitch",
                [new DbcMuxRange(3, 5)])]
        };
        var message = new DbcMessage(0x100, "M", 8, "ECU", false, [a, b]);
        var findings = DbcEngineeringValidator.Validate(message);
        Assert.Contains(findings, issue => issue.Code == "MUX_CONTROLLER_MISSING");
        Assert.Contains(findings, issue => issue.Code == "MUX_OVERLAP_REVIEW");
        Assert.DoesNotContain(findings, issue => issue.Code == "BIT_OVERLAP");
    }

    [Fact]
    public void InvalidOrUnrepresentableMuxRangesAreNotAssumedExclusive()
    {
        var controller = Signal("Switch", 0, 2) with { IsMultiplexor = true };
        var a = Signal("A", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch",
                [new DbcMuxRange(0, 2)])]
        };
        var b = Signal("B", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch",
                [new DbcMuxRange(4, 8)])]
        };
        var findings = DbcEngineeringValidator.Validate(
            new DbcMessage(0x100, "M", 8, "ECU", false,
                [controller, a, b]));
        Assert.Contains(findings, issue => issue.Code == "MUX_RANGE_OUT_OF_BOUNDS");
        Assert.Contains(findings, issue => issue.Code == "MUX_OVERLAP_REVIEW");

        var invalid = b with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch",
                [new DbcMuxRange(5, 4)])]
        };
        var invalidFindings = DbcEngineeringValidator.Validate(
            new DbcMessage(0x100, "M", 8, "ECU", false,
                [controller, a, invalid]));
        Assert.Contains(invalidFindings, issue => issue.Code == "MUX_RANGE_INVALID");
        Assert.Contains(invalidFindings, issue => issue.Code == "MUX_OVERLAP_REVIEW");
    }

    [Fact]
    public void SimpleMuxBranchesMustHaveUniqueValidRootAndRepresentableCodes()
    {
        var branch = Signal("ModeBranch", 8, 8, mux: 5);
        var withoutRoot = new DbcMessage(0x100, "NoMux", 2,
            "ECU", false, [branch]);
        Assert.Contains(DbcEngineeringValidator.Validate(withoutRoot),
            issue => issue.Signal == "ModeBranch" &&
                issue.Code == "MUX_ROOT_UNRESOLVED");

        var smallRoot = Signal("SmallRoot", 0, 2) with { IsMultiplexor = true };
        var tooLarge = new DbcMessage(0x100, "SmallMux", 2,
            "ECU", false, [smallRoot, branch]);
        Assert.Contains(DbcEngineeringValidator.Validate(tooLarge),
            issue => issue.Signal == "ModeBranch" &&
                issue.Code == "MUX_BRANCH_OUT_OF_BOUNDS");

        var validBranch = branch with { MultiplexValue = 3 };
        var valid = new DbcMessage(0x100, "ValidMux", 2,
            "ECU", false, [smallRoot, validBranch]);
        Assert.DoesNotContain(DbcEngineeringValidator.Validate(valid),
            issue => issue.Code is "MUX_ROOT_UNRESOLVED" or "MUX_BRANCH_OUT_OF_BOUNDS");
    }

    [Fact]
    public void FloatClassicMuxRootMustNotBeTreatedAsAnIntegerSelector()
    {
        var root = Signal("FloatingRoot", 0, 32) with
        {
            IsMultiplexor = true,
            ValueType = DbcSignalValueType.Float32
        };
        var branch = Signal("Branch", 32, 8, mux: 1);
        var message = new DbcMessage(0x100, "FloatRoot", 8, "ECU",
            false, [root, branch]);
        Assert.Contains(DbcEngineeringValidator.Validate(message),
            issue => issue.Code == "MUX_ROOT_FLOAT");
    }

    [Fact]
    public void ExtendedMuxReferenceToOrdinarySignalIsReviewNotProofOfExclusivity()
    {
        var notController = Signal("Switch", 0, 3);
        var a = Signal("A", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch",
                [new DbcMuxRange(0, 1)])]
        };
        var b = Signal("B", 8, 8) with
        {
            MultiplexConditions = [new DbcMultiplexCondition("Switch",
                [new DbcMuxRange(2, 3)])]
        };
        var findings = DbcEngineeringValidator.Validate(
            new DbcMessage(0x100, "M", 8, "ECU", false,
                [notController, a, b]));
        Assert.Contains(findings, issue => issue.Code == "MUX_CONTROLLER_NOT_SELECTOR");
        Assert.Contains(findings, issue => issue.Code == "MUX_OVERLAP_REVIEW");
    }
}
