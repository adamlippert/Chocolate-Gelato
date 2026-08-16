namespace Gelato.Collections;

/// <summary>
/// Two distinct limits.
///
/// <para><see cref="RowLimit"/> truncates one collection's membership.</para>
/// <para><see cref="RemainingBudget"/> caps how many item rows this feature may create
/// in total, across every collection. On reaching it, sync stops creating rows and keeps
/// reconciling existing membership — nothing is deleted.</para>
/// </summary>
public static class CapPolicy
{
    /// <summary>Maximum members for one collection. Zero or less means unlimited.</summary>
    public static int RowLimit(int rowMaxItems) => rowMaxItems <= 0 ? int.MaxValue : rowMaxItems;

    /// <summary>
    /// How many new item rows may still be created. Zero or less ceiling means unlimited.
    /// Never returns a negative value.
    /// </summary>
    public static int RemainingBudget(int globalCeiling, int itemsAlreadyCreated) =>
        globalCeiling <= 0 ? int.MaxValue : Math.Max(0, globalCeiling - itemsAlreadyCreated);
}
