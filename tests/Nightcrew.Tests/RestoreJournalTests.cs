using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class RestoreJournalTests : IDisposable
{
    private static readonly PowerValues Owner = new(1, 1, 900, 600);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePolicy _policy = new(Owner);
    private readonly ListLog _log = new();

    private string RecordPath => Path.Combine(_dir, "restore.json");

    private RestoreJournal NewJournal() => new(_policy, RecordPath, _log);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Arm_captures_owner_values_then_writes_arm_values()
    {
        var journal = NewJournal();

        journal.Arm();

        Assert.True(journal.HasRecord);
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Equal(1, _policy.Writes);
        Assert.False(File.Exists(RecordPath + ".tmp"));
    }

    [Fact]
    public void Rearm_keeps_the_first_owner_values()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.Values = new PowerValues(2, 2, 60, 60);

        journal.Arm();

        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Equal(2, _policy.Writes);
    }

    [Fact]
    public void Restore_puts_owner_values_back_and_deletes_the_record()
    {
        NewJournal().Arm();
        var journal = NewJournal();

        Assert.True(journal.Restore());

        Assert.Equal(Owner, _policy.Values);
        Assert.False(journal.HasRecord);
        Assert.Null(journal.OwnerValues);
    }

    [Fact]
    public void Restore_keeps_fields_the_user_changed()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.Values = new PowerValues(0, 2, 300, 0);

        Assert.True(journal.Restore());

        Assert.Equal(new PowerValues(Owner.LidAc, 2, 300, Owner.StandbyDc), _policy.Values);
    }

    [Fact]
    public void Restore_skips_the_write_when_nothing_is_ours()
    {
        var journal = NewJournal();
        journal.Arm();
        var changed = new PowerValues(3, 3, 120, 120);
        _policy.Values = changed;

        Assert.True(journal.Restore());

        Assert.Equal(changed, _policy.Values);
        Assert.Equal(1, _policy.Writes);
        Assert.False(journal.HasRecord);
    }

    [Fact]
    public void Failed_write_keeps_the_record_for_a_retry()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.FailWrites = true;

        Assert.False(journal.Restore());

        Assert.True(journal.HasRecord);
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Single(_log.Warnings);

        _policy.FailWrites = false;
        Assert.True(journal.Restore());
        Assert.Equal(Owner, _policy.Values);
        Assert.False(journal.HasRecord);
    }

    [Fact]
    public void Restore_without_a_record_does_nothing()
    {
        var journal = NewJournal();

        Assert.True(journal.Restore());

        Assert.False(journal.HasRecord);
        Assert.Null(journal.OwnerValues);
        Assert.Equal(0, _policy.Writes);
        Assert.Equal(Owner, _policy.Values);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    [InlineData("{\"OwnerLidAc\":\"x\"}")]
    public void Corrupt_record_is_set_aside_and_restore_fails(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, text);
        var journal = NewJournal();

        Assert.Null(journal.OwnerValues);
        Assert.False(journal.Restore());

        Assert.False(journal.HasRecord);
        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.Single(_log.Warnings);
        Assert.Equal(0, _policy.Writes);
    }

    [Fact]
    public void Arm_over_a_corrupt_record_captures_fresh_owner_values()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, "{ not json");
        var journal = NewJournal();

        journal.Arm();

        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Record_names_the_plan_it_armed()
    {
        NewJournal().Arm();

        Assert.Contains(_policy.Active.ToString("D"), File.ReadAllText(RecordPath));
    }

    [Fact]
    public void Restore_writes_back_to_the_recorded_plan_even_when_another_is_active()
    {
        var a = _policy.Active;
        var other = new PowerValues(2, 2, 300, 300);
        var b = _policy.AddScheme(other);
        var journal = NewJournal();
        journal.Arm();
        _policy.Activate(b);

        Assert.True(journal.Restore());

        Assert.Equal(Owner, _policy.ValuesOf(a));
        Assert.Equal(other, _policy.ValuesOf(b));
        Assert.False(journal.HasRecord);
    }

    [Fact]
    public void Arm_after_a_plan_switch_restores_the_old_plan_and_arms_the_new_one()
    {
        var a = _policy.Active;
        var other = new PowerValues(2, 2, 300, 300);
        var b = _policy.AddScheme(other);
        var journal = NewJournal();
        journal.Arm();
        _policy.Activate(b);
        Assert.True(journal.SchemeChanged);

        journal.Arm();

        Assert.Equal(Owner, _policy.ValuesOf(a));
        Assert.Equal(RestoreJournal.ArmValues, _policy.ValuesOf(b));
        Assert.Equal(other, journal.OwnerValues);
        Assert.False(journal.SchemeChanged);
    }

    [Fact]
    public void Plan_switch_keeps_the_old_record_when_the_old_plan_cannot_be_restored()
    {
        var a = _policy.Active;
        var b = _policy.AddScheme(new PowerValues(2, 2, 300, 300));
        var journal = NewJournal();
        journal.Arm();
        _policy.Activate(b);
        _policy.FailWritesTo(a);

        Assert.Throws<InvalidOperationException>(journal.Arm);

        Assert.Contains(a.ToString("D"), File.ReadAllText(RecordPath));
        Assert.Equal(Owner, journal.OwnerValues);
    }

    [Fact]
    public void Old_record_without_a_scheme_restores_the_active_plan()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath,
            "{\"OwnerLidAc\":1,\"OwnerLidDc\":1,\"OwnerStandbyAc\":900,\"OwnerStandbyDc\":600," +
            "\"WrittenLidAc\":0,\"WrittenLidDc\":0,\"WrittenStandbyAc\":0,\"WrittenStandbyDc\":0}");
        _policy.Values = new PowerValues(0, 0, 0, 0);

        Assert.True(NewJournal().Restore());

        Assert.Equal(Owner, _policy.Values);
    }

    private sealed class ListLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }

        public void Warn(string message) => Warnings.Add(message);
    }
}
