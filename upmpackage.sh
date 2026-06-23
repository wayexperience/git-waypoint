#!/bin/sh -eu
{ set +x; } 2>/dev/null
SOURCE="${BASH_SOURCE[0]}"
DIR="$( cd -P "$( dirname "$SOURCE" )" >/dev/null 2>&1 && pwd )"

dotnet tool install -g nbgv || true
pushd $DIR/src/it.wayexperience.unity.git-waypoint.api
version=$(nbgv get-version|grep AssemblyInformationalVersion|cut -d' ' -f2)
popd

echo "Packaging version $version"
scripts/create-packages.sh -v $version -t "$DIR/PackageSources" -u -p

package() {
	local PACKAGE=$1
	powershell scripts/CreateCombinedManifest.ps1 $PACKAGE PackageSources/$PACKAGE
}

package "it.wayexperience.unity.git-waypoint.api"
package "it.wayexperience.unity.git-waypoint.ui"
