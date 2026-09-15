using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace Unity.Services.Wire.Editor
{
    /// <summary>
    /// Contributes Wire's <c>link.xml</c> to the player build, so that the managed code Wire needs
    /// at runtime survives managed code stripping. We can't ship our own <c>link.xml</c> as-is
    /// since the Unity linker ignores those. This is the same strategy employed by the
    /// <c>com.unity.services.core</c> package.
    /// </summary>
    internal class WireLinkFileGenerator : IUnityLinkerProcessor
    {
        private const string k_LinkerFileName = "link.xml";

        internal static readonly string LinkerFolderPath = Path.Combine(".", "Library", "com.unity.services.wire");

        internal static readonly string LinkerFilePath = Path.Combine(LinkerFolderPath, k_LinkerFileName);

        internal const string LinkXmlContent =
@"<linker>
  <!-- Generated automatically by the com.unity.services.wire package. -->
  <assembly fullname=""System.Configuration"">
    <type fullname=""System.Configuration.ExeConfigurationHost"" preserve=""fields"">
      <method signature=""System.Void .ctor()"" />
    </type>
    <type fullname=""System.Configuration.IgnoreSection"" preserve=""all"" />
  </assembly>
  <assembly fullname=""System"">
    <namespace fullname=""System.Net.Configuration"" preserve=""all"" />
    <type fullname=""System.Diagnostics.SystemDiagnosticsSection"" preserve=""nothing"">
      <method name="".ctor"" />
    </type>
    <type fullname=""System.Diagnostics.FilterElement"" preserve=""nothing"">
      <method name="".ctor"" />
    </type>
    <type fullname=""System.Diagnostics.DiagnosticsConfigurationHandler"" preserve=""all"" />
    <type fullname=""System.Net.IPAddress"" preserve=""fields"" />
  </assembly>
</linker>
";

        int IOrderedCallback.callbackOrder { get; }

        string IUnityLinkerProcessor.GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            return GenerateAdditionalLinkXmlFile(data.target);
        }

        internal static string GenerateAdditionalLinkXmlFile(BuildTarget target)
        {
            // WebGL doesn't use websocket-sharp which is what we need link.xml for.
            if (target == BuildTarget.WebGL)
            {
                if (File.Exists(LinkerFilePath))
                {
                    File.Delete(LinkerFilePath);
                }

                return null;
            }

            if (!Directory.Exists(LinkerFolderPath))
            {
                Directory.CreateDirectory(LinkerFolderPath);
            }

            File.WriteAllText(LinkerFilePath, LinkXmlContent);

            return Path.GetFullPath(LinkerFilePath);
        }
    }
}
