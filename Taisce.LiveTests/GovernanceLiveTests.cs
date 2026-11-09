// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using Taisce;
using Xunit;

namespace Taisce.LiveTests;

// Erasure and export against a real deployment: the properties D24 names. A mock would prove that
// the mock matches the assertion, and what these claim is about what the deployment deleted.
//
// Not in the default `dotnet test` of the solution's CI job, which has no deployment. The target is
// TAISCE_API; TAISCE_TOKEN is a read-write key and TAISCE_READER_TOKEN a read-only one. A missing
// variable FAILS the run. There is no skip-when-absent arm: a green run that skipped the tests would
// assert nothing, and would read as proof.
public class GovernanceLiveTests
{
    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"{name} is not set: these tests need a real deployment, a read-write key and a read-only key, and do not skip without them");

    private static TaisceClient Writer() => new(Require("TAISCE_API"), Require("TAISCE_TOKEN"));

    private static TaisceClient Reader() => new(Require("TAISCE_API"), Require("TAISCE_READER_TOKEN"));

    // A subject nobody has used, so one test's erasure cannot be another's residue.
    private static string FreshSubject() => "live-" + Guid.NewGuid().ToString("N");

    private static async Task<ObserveReceipt> ObserveTurnAsync(TaisceClient client, string subject, string user)
    {
        var messages = new[] { new ObserveMessage("user", user), new ObserveMessage("assistant", "Noted.") };
        return await client.ObserveAsync(new ObserveRequest
        {
            IdempotencyKey = TurnKey.Derive(subject, "live-tests", messages),
            DataSubjectId = subject,
            Messages = messages,
        });
    }

    [Fact]
    public async Task AnErasureBySubjectReturnsACleanReceiptWithEveryResidualZeroAndSomethingDeleted()
    {
        using var client = Writer();
        var subject = FreshSubject();
        await ObserveTurnAsync(client, subject, "Where does Marta work?");

        var receipt = await client.EraseSubjectAsync(subject, "live test");

        Assert.True(receipt.Clean);
        Assert.Equal(subject, receipt.DataSubjectId);
        Assert.NotEmpty(receipt.RequestId);
        Assert.NotEmpty(receipt.Residual);
        Assert.All(receipt.Residual, r => Assert.True(r.Value == 0, $"residual {r.Key}={r.Value}"));
        Assert.True(receipt.Deleted.Values.Sum() > 0, "the erasure deleted nothing");

        // The deployment's own export agrees: nothing is left for the subject.
        var after = await client.ExportSubjectAsync(subject);
        Assert.All(after.Rows, r => Assert.True(r.Value == 0, $"export still holds {r.Key}={r.Value}"));
    }

    [Fact]
    public async Task AnErasureBySourcesErasesTheObservationItNames()
    {
        using var client = Writer();
        var subject = FreshSubject();
        var kept = await ObserveTurnAsync(client, subject, "A turn that stays.");
        var named = await ObserveTurnAsync(client, subject, "A turn that goes.");

        var receipt = await client.EraseSourcesAsync([named.Id], "live test");

        Assert.True(receipt.Clean);
        Assert.Contains(named.Id, receipt.SourceObservationIds!);
        Assert.True(receipt.Deleted.Values.Sum() > 0, "the erasure deleted nothing");
        var gone = await client.ExportSourcesAsync([named.Id]);
        Assert.All(gone.Rows, r => Assert.True(r.Value == 0, $"export still holds {r.Key}={r.Value}"));
        // The observation it did not name is still there.
        var stays = await client.ExportSourcesAsync([kept.Id]);
        Assert.True(stays.Rows.Values.Sum() > 0, "the erasure reached an observation it did not name");

        await client.EraseSubjectAsync(subject, "live test cleanup");
    }

    [Fact]
    public async Task AReadOnlyKeyIsRefusedOnEraseAndAllowedOnExport()
    {
        using var writer = Writer();
        using var reader = Reader();
        var subject = FreshSubject();
        await ObserveTurnAsync(writer, subject, "Where does Marta work?");

        await Assert.ThrowsAsync<TaisceForbiddenException>(() => reader.EraseSubjectAsync(subject, "live test"));
        var export = await reader.ExportSubjectAsync(subject);

        Assert.True(export.Rows.Values.Sum() > 0, "the read-only export was empty");
        // The refusal left everything in place.
        Assert.True((await writer.ExportSubjectAsync(subject)).Rows.Values.Sum() > 0);

        await writer.EraseSubjectAsync(subject, "live test cleanup");
    }

    [Fact]
    public async Task AnExportsRowCountsMatchTheRowsOfEachSection()
    {
        using var client = Writer();
        var subject = FreshSubject();
        await ObserveTurnAsync(client, subject, "Where does Marta work?");

        var export = await client.ExportSubjectAsync(subject);

        Assert.NotEmpty(export.Sections);
        Assert.Equal(export.Sections.Keys.Order(), export.Rows.Keys.Order());
        foreach (var (name, rows) in export.Sections)
        {
            Assert.Equal(rows.Count, export.Rows[name]);
        }
        Assert.True(export.Rows.Values.Sum() > 0);

        await client.EraseSubjectAsync(subject, "live test cleanup");
    }
}
