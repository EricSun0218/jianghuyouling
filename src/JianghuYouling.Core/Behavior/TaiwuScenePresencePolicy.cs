namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Canonical physical-presence classification used by the backend race check
    /// and exercised without game assemblies by the offline harness.
    /// </summary>
    public static class TaiwuScenePresencePolicy
    {
        public static bool IsPresent(bool isTaiwu, bool isCompanion,
            bool kidnappedByTaiwu, bool inTaiwuVillagePrisonWithTaiwuPresent,
            bool sameValidLocation)
            => isTaiwu || isCompanion || kidnappedByTaiwu
                || inTaiwuVillagePrisonWithTaiwuPresent || sameValidLocation;
    }
}
