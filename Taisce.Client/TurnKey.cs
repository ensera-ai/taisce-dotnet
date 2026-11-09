// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;

namespace Taisce;

/// <summary>
/// The idempotency key of a turn: the one definition every Taisce adapter derives the same way.
/// </summary>
/// <remarks>
/// <para>Each field is written as a netstring (its length in UTF-8 bytes, <c>:</c>, the bytes, <c>,</c>)
/// and the netstrings of the derivation's name, the data subject, the run and then each message's
/// role and content are hashed together. A length cannot be forged by content. Fields joined by a
/// separator can: with a newline between them, a turn whose content contains the newline and a role
/// name hashes the same bytes as a different turn, and the deployment then refuses the second as a
/// conflicting retry of the first on every attempt.</para>
/// <para>The length is a byte count of the UTF-8 encoding and never <see cref="string.Length"/>,
/// which counts UTF-16 units and so disagrees with the Java and Python adapters for every character
/// outside the Basic Multilingual Plane. The vectors in <c>turn-keys.json</c> include one.</para>
/// </remarks>
public static class TurnKey
{
    /// <summary>The derivation's name, hashed as the first field so a change of definition changes every key.</summary>
    public const string Derivation = "taisce-turn-key/2";

    /// <summary>A version-5-shaped UUID over the subject, the run and the messages.</summary>
    /// <param name="dataSubjectId">The person the turn is about; null is the empty string.</param>
    /// <param name="runId">The run the turn belongs to; null is the empty string.</param>
    /// <param name="messages">The observed messages, in order.</param>
    public static string Derive(string? dataSubjectId, string? runId, IReadOnlyList<ObserveMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Derivation);
        Append(hash, dataSubjectId ?? "");
        Append(hash, runId ?? "");
        foreach (var m in messages)
        {
            Append(hash, m.Role);
            Append(hash, m.Content);
        }
        var bytes = hash.GetHashAndReset()[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    private static void Append(IncrementalHash hash, string field)
    {
        var body = Encoding.UTF8.GetBytes(field);
        hash.AppendData(Encoding.ASCII.GetBytes(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
        hash.AppendData(body);
        hash.AppendData(","u8);
    }
}
