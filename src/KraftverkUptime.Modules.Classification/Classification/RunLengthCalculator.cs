namespace KraftverkUptime.Modules.Classification.Classification;

/// <summary>
/// Beregner run-length for hver posisjon i en boolsk sekvens.
/// For hver indeks i: hvor lang er den sammenhengende true-sekvensen som
/// posisjonen tilhører (eller 0 hvis arr[i] = false).
///
/// Brukes av klassifiseringen til å identifisere sammenhengende null-
/// produksjons-blokker (kandidater for PlannedOutage).
/// </summary>
public static class RunLengthCalculator
{
    public static int[] Compute(ReadOnlySpan<bool> input)
    {
        var n = input.Length;
        var result = new int[n];
        if (n == 0)
        {
            return result;
        }

        // Forlengs-pass: hver posisjon får lengden så langt av sin run
        var current = 0;
        for (var i = 0; i < n; i++)
        {
            current = input[i] ? current + 1 : 0;
            result[i] = current;
        }

        // Baklengs-pass: utvid slik at alle posisjoner i en run får full run-lengde
        var i2 = n - 1;
        while (i2 >= 0)
        {
            if (result[i2] > 0)
            {
                var runLen = result[i2];
                var runEnd = i2;
                var runStart = runEnd - runLen + 1;
                for (var k = runStart; k <= runEnd; k++)
                {
                    result[k] = runLen;
                }
                i2 = runStart - 1;
            }
            else
            {
                i2--;
            }
        }

        return result;
    }
}
