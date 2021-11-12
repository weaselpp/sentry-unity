#!/bin/bash
cd $(dirname "$0")
REPO=getsentry/sentry-cli
VERSION=1.70.0
PLATFORMS="Darwin-universal Linux-x86_64 Windows-x86_64"
# Linux-x86_64 Windows-i686
TARGETDIR="../package-dev/Editor/sentry-cli/"

rm -f ${TARGETDIR}*
for plat in $PLATFORMS; do
  suffix=''
  if [[ $plat == *"Windows"* ]]; then
    suffix='.exe'
  fi
  echo "${plat}"
  download_url=https://github.com/$REPO/releases/download/$VERSION/sentry-cli-${plat}${suffix}
  fn="${TARGETDIR}/${plat}${suffix}"
  curl -SL --progress-bar "$download_url" -o "$fn"
  chmod +x "$fn"
  sha1sum ${TARGETDIR}/* > ${TARGETDIR}/checksums.sha || shasum ${TARGETDIR}/* > ${TARGETDIR}/checksums.sha
done