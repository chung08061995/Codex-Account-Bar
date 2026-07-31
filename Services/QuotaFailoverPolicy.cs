using CodexAccountBar.Models;

namespace CodexAccountBar.Services;

public static class QuotaFailoverPolicy
{
    public const double MinimumCandidateRemaining = 5;

    public static bool IsExhausted(AccountRecord? account) =>
        EffectiveRemaining(account) is { } remaining && remaining <= 0.5;

    public static AccountRecord? BestCandidate(
        IEnumerable<AccountRecord> accounts,
        string excludingAccountId
    ) => accounts
        .Where(account => account.Id != excludingAccountId)
        .Select(account => (Account: account, Remaining: EffectiveRemaining(account)))
        .Where(item => item.Remaining > MinimumCandidateRemaining)
        .OrderByDescending(item => item.Remaining)
        .ThenBy(item => item.Account.Email, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Account)
        .FirstOrDefault();

    public static double? EffectiveRemaining(AccountRecord? account)
    {
        if (account is null) return null;
        var remaining = account.PrimaryLeft;
        if (account.HasSecondary) remaining = Math.Min(remaining, account.SecondaryLeft);
        return remaining;
    }
}
