using MeshTextBaker;
using UnityEngine;

public sealed class MtbLocalizationSmokeControls : MonoBehaviour
{
    [SerializeField]
    private ExternalOverrideLocalizationProvider provider;

    public void SetEnglish()
    {
        provider.SetLocale("en");
    }

    public void SetRussian()
    {
        provider.SetLocale("ru");
    }

    public void ReloadExternalFiles()
    {
        provider.ReloadLocalization();
    }
}