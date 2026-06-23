#pragma warning disable 436

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("it.wayexperience.unity.git-waypoint.tests", AllInternalsVisible = true)]
[assembly: InternalsVisibleTo("it.wayexperience.unity.git-waypoint.ui.tests", AllInternalsVisible = true)]

[assembly: InternalsVisibleTo("TestUtils", AllInternalsVisible = true)]

[assembly: InternalsVisibleTo("UnityApiTests", AllInternalsVisible = true)]
[assembly: InternalsVisibleTo("UnityUITests", AllInternalsVisible = true)]

[assembly: InternalsVisibleTo("Unit.Tests", AllInternalsVisible = true)]
[assembly: InternalsVisibleTo("Integration.Tests", AllInternalsVisible = true)]

//Required for NSubstitute
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2", AllInternalsVisible = true)]

//Required for Unity compilation
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor", AllInternalsVisible = true)]
