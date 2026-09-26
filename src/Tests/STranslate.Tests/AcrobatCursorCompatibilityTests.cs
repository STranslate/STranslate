using STranslate.Helpers;

namespace STranslate.Tests;

public class AcrobatCursorCompatibilityTests
{
    // 分别来自普通采样与 PerMonitorV2 下实际 Acrobat 拖选，不能互相代替。
    private const string Text37 = "6:12:37x37x1-1207FBF437A6D60BAD608C9C4A7397194C4F3768142A32C7E5F3A1415452A992:37x37x32-8B594E3DDBF4F7ADCB4EA9DB36D5FE599C6C336AC46A4CFC4E166D5C862A1B60";
    private const string Text56 = "9:18:56x56x1-5C55C8F4DB4010BA9203D83536D0609856AF8C847AC039E37E7DDE8FBD574B61:56x56x32-5600571C565EC241354317B795788BB262735B4026608F2F58B283C813B692AF";
    private const string Other56 = "4:7:56x56x1-5C55C8F4DB4010BA9203D83536D0609856AF8C847AC039E37E7DDE8FBD574B61:56x56x32-58798ACC0383D7EB89B5CC523EBB1D2BEFE7A6F2EEE0F8F90019E3ABDEAC317B";

    [Theory]
    [InlineData(Text37, true)]
    [InlineData(Text56, true)]
    [InlineData(Other56, false)]
    [InlineData("", false)]
    [InlineData("error", false)]
    public void MatchesOnlyKnownTextShapes(string fingerprint, bool expected)
        => Assert.Equal(expected, AcrobatCursorCompatibility.MatchesFingerprint(fingerprint));

    [Theory]
    [InlineData("Acrobat", true)]
    [InlineData("AcroRd32", true)]
    [InlineData("ACROBAT", true)]
    [InlineData("AcrobatHelper", false)]
    [InlineData("msedge", false)]
    [InlineData("", false)]
    public void LimitsFallbackToAcrobat(string processName, bool expected)
    {
        var fixture = new Fixture { ProcessName = processName };
        Assert.Equal(expected, fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.Equal(expected ? 1 : 0, fixture.FingerprintReads);
    }

    [Fact]
    public void DoesNotReadProcessesOrBitmapsForNullCursor()
    {
        var fixture = new Fixture();
        Assert.False(fixture.Detector.IsTextCursor(IntPtr.Zero));
        Assert.Equal(0, fixture.ProcessReads);
        Assert.Equal(0, fixture.FingerprintReads);
    }

    [Fact]
    public void CachesRepeatedReadsAndRefreshesReusedHandles()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        fixture.Fingerprint = Other56;
        fixture.Time = 1999;
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.Equal(1, fixture.FingerprintReads);
        fixture.Time = 2000;
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.Equal(2, fixture.FingerprintReads);
        Assert.Equal(2, fixture.ProcessReads);
    }

    [Fact]
    public void SwitchingProcessesInvalidatesCursorCache()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        fixture.Pid++;
        fixture.Fingerprint = Other56;
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.Equal(2, fixture.FingerprintReads);
    }

    [Fact]
    public void RechecksReusedProcessIds()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        fixture.ProcessName = "notepad";
        fixture.Time = 2000;
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.Equal(1, fixture.FingerprintReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedRefreshDoesNotReuseSuccessfulResult(bool processFailure)
    {
        var fixture = new Fixture();
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        fixture.Time = 2000;
        fixture.FailProcessRead = processFailure;
        fixture.FailFingerprintRead = !processFailure;
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)1));
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)1));
    }

    [Fact]
    public void NewCursorHandleIsCheckedImmediately()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Detector.IsTextCursor((IntPtr)1));
        fixture.Fingerprint = Other56;
        Assert.False(fixture.Detector.IsTextCursor((IntPtr)2));
        Assert.Equal(2, fixture.FingerprintReads);
    }

    private sealed class Fixture
    {
        public uint Pid = 1;
        public string ProcessName = "Acrobat";
        public string Fingerprint = Text56;
        public long Time;
        public int ProcessReads;
        public int FingerprintReads;
        public bool FailProcessRead;
        public bool FailFingerprintRead;
        public AcrobatCursorCompatibility Detector { get; }

        public Fixture()
        {
            Detector = new(() => Pid, _ =>
            {
                ProcessReads++;
                if (FailProcessRead) throw new InvalidOperationException();
                return ProcessName;
            }, _ =>
            {
                FingerprintReads++;
                if (FailFingerprintRead) throw new InvalidOperationException();
                return Fingerprint;
            }, () => Time);
        }
    }
}
