namespace Praxis.Domain.Finance;

/// <summary>One group of an exact aggregation: key, signed total in minor units, inflow and outflow totals and count.</summary>
public sealed record LedgerAggregate(string Key, string Currency, long TotalMinor, long InflowMinor, long OutflowMinor, int Count);
