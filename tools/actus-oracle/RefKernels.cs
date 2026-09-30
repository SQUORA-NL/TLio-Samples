using ActusInsurance.LifeInsurance.GPU.Kernels;
using ActusInsurance.LifeInsurance.GPU.Models;

namespace ActusOracle;

/// <summary>
/// Straight C# reimplementations of LifeProjectionKernel.Kernel, used only to measure how far a
/// double-precision port can be from the kernel's own output.
///  - Faithful: identical float/double mix to the kernel (age, years-in-force, table lookups in float32).
///  - DoubleFloatTables: everything double, but the tables are the float32 tables the kernel uses
///    (exactly what a Tlio script fed the exported tables would compute).
///  - DoubleDoubleTables: everything double, and the tables rebuilt from the analytic formulas in double.
/// </summary>
public static class RefKernels
{
    public enum Mode { Faithful, DoubleFloatTables, DoubleDoubleTables }

    public sealed class Tables
    {
        public float[] Mortality = null!, Lapse = null!, Disability = null!;
        public int[] Hazards = null!;
        public double[] MortalityD = null!, LapseD = null!, DisabilityD = null!;
    }

    public static Tables BuildTables()
    {
        var t = new Tables
        {
            Mortality = LifeGpuParameterBuilder.BuildMortalityTable(),
            Lapse = LifeGpuParameterBuilder.BuildLapseTable(),
            Disability = LifeGpuParameterBuilder.BuildDisabilityTable(),
            Hazards = LifeGpuParameterBuilder.BuildMarkovHazards(),
        };
        // Same formulas as LifeGpuParameterBuilder, without the (float) cast.
        const double A = 0.0007, B = 0.00005, lnC = 0.08617196929518;
        t.MortalityD = new double[LifeGpuParameterBuilder.MortalityAgeCount * 3];
        t.DisabilityD = new double[LifeGpuParameterBuilder.MortalityAgeCount * 3];
        for (var a = 0; a < LifeGpuParameterBuilder.MortalityAgeCount; a++)
        {
            double age = LifeGpuParameterBuilder.MortalityAgeMin + a * LifeGpuParameterBuilder.MortalityAgeStep;
            double muM = A + B * Math.Exp(age * lnC), muF = muM * 0.85;
            t.MortalityD[a * 3] = 1.0 - Math.Exp(-muM);
            t.MortalityD[a * 3 + 1] = 1.0 - Math.Exp(-muF);
            t.MortalityD[a * 3 + 2] = 1.0 - Math.Exp(-muF);
            double baseM = Math.Min(0.003 + 0.0002 * Math.Max(0.0, age - 30.0), 0.05);
            double baseF = Math.Min(baseM * 1.1, 0.05);
            t.DisabilityD[a * 3] = baseM;
            t.DisabilityD[a * 3 + 1] = baseF;
            t.DisabilityD[a * 3 + 2] = baseF;
        }
        double[] mf = { 1.00, 0.95, 0.90 };
        t.LapseD = new double[LifeGpuParameterBuilder.LapseDurCount * 3];
        for (var d = 0; d < LifeGpuParameterBuilder.LapseDurCount; d++)
        {
            float dur = LifeGpuParameterBuilder.LapseDurMin + d * LifeGpuParameterBuilder.LapseDurStep;
            double baseLapse = dur < 0.5f ? 0.08 : dur < 1.0f ? 0.07 : dur < 2.0f ? 0.06 : dur < 3.0f ? 0.04 : dur < 5.0f ? 0.03 : dur < 10.0f ? 0.025 : 0.02;
            for (var m = 0; m < 3; m++) t.LapseD[d * 3 + m] = baseLapse * mf[m];
        }
        return t;
    }

    public static (double[] Cf, double[] Pa, double[] Pd, double[] Pl) Run(
        in LifeContractGpu c, int steps, double dt, Tables tb, Mode mode)
    {
        var cf = new double[steps]; var pa = new double[steps]; var pd = new double[steps]; var pl = new double[steps];

        if (c.CurrentState == 6 || c.CurrentState == 7 || c.CurrentState == 9)
        {
            for (var t = 0; t < steps; t++) { cf[t] = 0; pa[t] = 0; pd[t] = 1; pl[t] = 0; }
            return (cf, pa, pd, pl);
        }

        bool hasM = false, hasL = false, hasD = false;
        var from = (int)c.CurrentState;
        if (from < 10)
            for (var ts = 0; ts < 10; ts++)
            {
                var h = tb.Hazards[from * 10 + ts];
                if (h == LifeGpuParameterBuilder.HazardMortality) hasM = true;
                else if (h == LifeGpuParameterBuilder.HazardLapse) hasL = true;
                else if (h == LifeGpuParameterBuilder.HazardDisability) hasD = true;
            }

        double smoker = c.SmokerStatus != 0u ? 1.35 : 1.0;
        double uw = 1.0 + c.ExtraPremBps / 10_000.0;
        double modeF = c.PremiumMode == 0u ? 12.0 : c.PremiumMode == 1u ? 4.0 : 1.0;
        double annual = c.PremiumAmount * modeF;

        double act = 1.0, dead = 0.0, lapsed = 0.0;
        for (var t = 0; t < steps; t++)
        {
            double qx = 0, lapse = 0, dis = 0;
            if (mode == Mode.Faithful)
            {
                float ageT = c.AgeAtEval + (float)(t * dt);
                float yifT = c.YearsInForce + (float)(t * dt);
                if (hasM)
                {
                    qx = LookupF(tb.Mortality, ageT, LifeGpuParameterBuilder.MortalityAgeMin, LifeGpuParameterBuilder.MortalityAgeStep, LifeGpuParameterBuilder.MortalityAgeCount, (int)c.InsuredGender) * smoker * uw;
                    if (qx > 0.999) qx = 0.999; if (qx < 0.0) qx = 0.0;
                }
                if (hasL) lapse = LookupF(tb.Lapse, yifT, LifeGpuParameterBuilder.LapseDurMin, LifeGpuParameterBuilder.LapseDurStep, LifeGpuParameterBuilder.LapseDurCount, (int)c.PremiumMode);
                if (hasD)
                {
                    dis = LookupF(tb.Disability, ageT, LifeGpuParameterBuilder.MortalityAgeMin, LifeGpuParameterBuilder.MortalityAgeStep, LifeGpuParameterBuilder.MortalityAgeCount, (int)c.InsuredGender) * smoker;
                    if (dis > 0.05) dis = 0.05; if (dis < 0.0) dis = 0.0;
                }
            }
            else
            {
                double ageT = (double)c.AgeAtEval + t * dt;
                double yifT = (double)c.YearsInForce + t * dt;
                var mort = mode == Mode.DoubleFloatTables ? tb.Mortality.Select(x => (double)x).ToArray() : tb.MortalityD;
                var lap = mode == Mode.DoubleFloatTables ? tb.Lapse.Select(x => (double)x).ToArray() : tb.LapseD;
                var disT = mode == Mode.DoubleFloatTables ? tb.Disability.Select(x => (double)x).ToArray() : tb.DisabilityD;
                if (hasM)
                {
                    qx = LookupD(mort, ageT, 20.0, 1.0, 61, (int)c.InsuredGender) * smoker * uw;
                    if (qx > 0.999) qx = 0.999; if (qx < 0.0) qx = 0.0;
                }
                if (hasL) lapse = LookupD(lap, yifT, 0.0, 1.0, 31, (int)c.PremiumMode);
                if (hasD)
                {
                    dis = LookupD(disT, ageT, 20.0, 1.0, 61, (int)c.InsuredGender) * smoker;
                    if (dis > 0.05) dis = 0.05; if (dis < 0.0) dis = 0.0;
                }
            }

            double total = qx + lapse + dis;
            if (total > 1.0) total = 1.0;
            double pDeath = act * qx, pLapse = act * lapse, pStay = act * (1.0 - total);
            cf[t] = act * annual * dt - pDeath * c.SumAssured;
            pa[t] = pStay; pd[t] = dead + pDeath; pl[t] = lapsed + pLapse;
            dead += pDeath; lapsed += pLapse; act = pStay;
        }
        return (cf, pa, pd, pl);
    }

    private static double LookupF(float[] table, float key, float keyMin, float keyStep, int count, int col)
    {
        float nk = (key - keyMin) / keyStep;
        if (nk < 0f) nk = 0f;
        int lower = (int)nk;
        if (lower >= count - 1) return table[(count - 1) * 3 + col];
        float frac = nk - lower;
        double v0 = table[lower * 3 + col], v1 = table[(lower + 1) * 3 + col];
        return v0 + frac * (v1 - v0);
    }

    private static double LookupD(double[] table, double key, double keyMin, double keyStep, int count, int col)
    {
        double nk = (key - keyMin) / keyStep;
        if (nk < 0.0) nk = 0.0;
        int lower = (int)nk;
        if (lower >= count - 1) return table[(count - 1) * 3 + col];
        double frac = nk - lower;
        double v0 = table[lower * 3 + col], v1 = table[(lower + 1) * 3 + col];
        return v0 + frac * (v1 - v0);
    }
}
