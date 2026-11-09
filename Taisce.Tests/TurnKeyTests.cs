// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using Taisce;
using Xunit;

namespace Taisce.Tests;

// The turn key is one definition across adapters. turn-keys.json is the shared proof: the same
// vectors are asserted in every adapter's tests, so a derivation that drifts in one language fails
// there rather than in a deployment refusing retries.
public class TurnKeyTests
{
    private sealed record Vector(string Name, string DataSubjectId, string Run, List<ObserveMessage> Messages, string Key);

    private static List<Vector> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "turn-keys.json")));
        Assert.Equal(TurnKey.Derivation, doc.RootElement.GetProperty("derivation").GetString());
        return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            v.GetProperty("data_subject_id").GetString()!,
            v.GetProperty("run").GetString()!,
            v.GetProperty("messages").EnumerateArray()
                .Select(m => new ObserveMessage(m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!)).ToList(),
            v.GetProperty("key").GetString()!)).ToList();
    }

    [Fact]
    public void EveryVectorInTheSharedFileDerivesItsKey()
    {
        var vectors = Vectors();
        Assert.Equal(8, vectors.Count);
        foreach (var v in vectors)
        {
            Assert.True(v.Key == TurnKey.Derive(v.DataSubjectId, v.Run, v.Messages), $"vector '{v.Name}'");
            // The adapter's entry point is the same derivation, not a second copy of it.
            Assert.Equal(v.Key, Taisce.AgentFramework.TaisceContextProvider.TurnKey(v.DataSubjectId, v.Run, v.Messages));
        }
    }

    [Fact]
    public void TwoTurnsWhoseLineJoinedTextIsIdenticalAreTwoKeys()
    {
        var vectors = Vectors();
        Assert.NotEqual(vectors[0].Key, vectors[1].Key);
        Assert.NotEqual(
            TurnKey.Derive(vectors[0].DataSubjectId, vectors[0].Run, vectors[0].Messages),
            TurnKey.Derive(vectors[1].DataSubjectId, vectors[1].Run, vectors[1].Messages));
    }

    // string.Length counts UTF-16 units: one character outside the BMP is 2 of them and 4 UTF-8 bytes.
    // A derivation that used the former would disagree with the Java and Python adapters on this
    // vector alone, which is why the file carries one.
    [Fact]
    public void ALengthIsCountedInUtf8BytesAndNotInUtf16Units()
    {
        var v = Vectors().Single(x => x.Name.Contains("Basic Multilingual Plane", StringComparison.Ordinal));
        Assert.Contains(v.Messages, m => m.Content.Any(char.IsSurrogate));
        Assert.Equal(v.Key, TurnKey.Derive(v.DataSubjectId, v.Run, v.Messages));
    }

    [Fact]
    public void AnAbsentSubjectAndRunAreTheEmptyString()
    {
        var turn = new[] { new ObserveMessage("user", "hello") };
        Assert.Equal(TurnKey.Derive("", "", turn), TurnKey.Derive(null, null, turn));
    }
}
