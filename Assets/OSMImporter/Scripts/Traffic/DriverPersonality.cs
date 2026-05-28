using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// DriverPersonality — Sliders and characteristics defining the driver's behavior.
    /// </summary>
    [System.Serializable]
    public class DriverPersonality
    {
        [Range(0f, 1f)] public float Aggression = 0.5f;
        [Range(0f, 1f)] public float Patience = 0.5f;
        [Range(0f, 1f)] public float RiskTolerance = 0.5f;
        [Range(0f, 1f)] public float Lawfulness = 0.5f;
        [Range(0f, 1f)] public float Opportunism = 0.5f;
        [Range(0f, 1f)] public float Assertiveness = 0.5f;
        [Range(0.5f, 2.0f)] public float ReactionTime = 1.0f; // Scale factor for safe reaction times
        [Range(0f, 1f)] public float Honkiness = 0.5f;
        [Range(0f, 1f)] public float LaneDiscipline = 0.5f;
        [Range(0f, 1f)] public float ReverseReluctance = 0.5f;

        public static DriverPersonality CreateRandom()
        {
            return new DriverPersonality
            {
                Aggression = Random.value,
                Patience = Random.value,
                RiskTolerance = Random.value,
                Lawfulness = Random.value,
                Opportunism = Random.value,
                Assertiveness = Random.value,
                ReactionTime = Random.Range(0.7f, 1.3f),
                Honkiness = Random.value,
                LaneDiscipline = Random.value,
                ReverseReluctance = Random.value
            };
        }
    }
}
