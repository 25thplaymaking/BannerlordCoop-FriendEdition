using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieCrimeRoomSnapshot
{
    private readonly bool hadAutoInvestment;
    private readonly object autoInvestment;

    public FourberieCrimeRoomSnapshot(IDictionary crime)
    {
        hadAutoInvestment = crime?.Contains(61) == true;
        autoInvestment = hadAutoInvestment ? crime[61] : null;
    }

    public void Restore(IDictionary crime)
    {
        if (crime == null) return;
        if (hadAutoInvestment) crime[61] = autoInvestment;
        else crime.Remove(61);
    }
}

internal static class FourberieCrimeRoomAuthority
{
    public static FourberieCrimeRoomSnapshot CaptureReadState(IDictionary crime) =>
        new FourberieCrimeRoomSnapshot(crime);

    public static bool TrySet(
        IDictionary crime,
        FourberieOperation operation,
        int value,
        out string failure)
    {
        int key;
        bool valid;
        switch (operation)
        {
            case FourberieOperation.SetCorruptionLevel:
                key = 5;
                valid = value == 1 || value == 2 || value == 3 || value == 10;
                break;
            case FourberieOperation.SetAutoInvestment:
                key = 61;
                valid = value >= 0 && value <= 5;
                break;
            case FourberieOperation.SetLadsDuty:
                key = 1000;
                valid = value >= 0 && value <= 100;
                break;
            case FourberieOperation.SetSlavesDuty:
                key = 1001;
                valid = value >= 0 && value <= 100;
                break;
            default:
                key = 0;
                valid = false;
                break;
        }

        if (crime == null || !valid)
        {
            failure = "invalid Fourberie crime-room setting";
            return false;
        }

        crime[key] = value;
        failure = null;
        return true;
    }
}
