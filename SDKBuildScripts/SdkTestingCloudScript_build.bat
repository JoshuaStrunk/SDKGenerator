pushd ..
if [%1] == [] (
rem === BUILDING NewTarget ===
node generate.js SdkTestingCloudScript=..\sdks\SdkTestingCloudScript -apiSpecGitUrl
) else (
rem === BUILDING NewTarget with params %* ===
node generate.js SdkTestingCloudScript=..\sdks\SdkTestingCloudScript %*
)
popd

pause
