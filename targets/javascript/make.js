var ejs = require("ejs");
var path = require("path");

exports.putInRoot = true;

exports.makeCombinedAPI = function (apis, sourceDir, apiOutputDir) {
    console.log("Generating JavaScript Combined SDK to " + apiOutputDir);
    
    var templateDir = path.resolve(sourceDir, "templates");
    var apiTemplate = ejs.compile(readFile(path.resolve(templateDir, "PlayFab_Api.js.ejs")));
    var apiTypingTemplate = ejs.compile(readFile(path.resolve(templateDir, "PlayFab_Api.d.ts.ejs")));
    
    copyTree(path.resolve(sourceDir, "source"), path.resolve(apiOutputDir, ".."));
    MakeSimpleTemplates(apis, templateDir, apiOutputDir);
    
    var apiLocals = {
        GenerateSummary: GenerateSummary,
        GetAuthParams: GetAuthParams,
        GetBaseTypeSyntax: GetBaseTypeSyntax,
        GetDeprecationAttribute: GetDeprecationAttribute,
        GetPropertyTsType: GetPropertyTsType,
        GetRequestActions: GetRequestActions,
        GetResultActions: GetResultActions,
        GetUrl: GetUrl,
        HasResultActions: HasResultActions,
        buildIdentifier: exports.buildIdentifier,
        sdkVersion: exports.sdkVersion
    };
    for (var i = 0; i < apis.length; i++) {
        apiLocals.api = apis[i];
        apiLocals.hasServerOptions = apis[i].name !== "Client"; // NOTE FOR THE EJS FILE: PlayFab.settings and PlayFab._internalSettings and are still global/shared - Only utilize this within the api-specific section
        apiLocals.hasClientOptions = apis[i].name === "Client"; // NOTE FOR THE EJS FILE: PlayFab.settings and PlayFab._internalSettings and are still global/shared - Only utilize this within the api-specific section
        var generatedApi = apiTemplate(apiLocals);
        writeFile(path.resolve(apiOutputDir, "PlayFabSDK/PlayFab" + apis[i].name + "Api.js"), generatedApi);
        var generatedTypings = apiTypingTemplate(apiLocals);
        writeFile(path.resolve(apiOutputDir, "TsTypings/PlayFab" + apis[i].name + "Api.d.ts"), generatedTypings);
    }
}

function MakeSimpleTemplates(apis, templateDir, apiOutputDir) {
    var apiLocals = {
        apis: apis
    };
    var coreTyping = ejs.compile(readFile(path.resolve(templateDir, "PlayFab.d.ts.ejs")));
    var genCoreTypings = coreTyping(apiLocals);
    writeFile(path.resolve(apiOutputDir, "TsTypings/Playfab.d.ts"), genCoreTypings);
}

function GetRequestActions(apiCall, api) {
    if (api.name === "Client" && (apiCall.result === "LoginResult" || apiCall.request === "RegisterPlayFabUserRequest"))
        return "        request.TitleId = PlayFab.settings.titleId != null ? PlayFab.settings.titleId : request.TitleId; if (request.TitleId == null) throw \"Must be have PlayFab.settings.titleId set to call this method\";\n";
    if (api.name === "Client" && apiCall.auth === "SessionTicket")
        return "        if (PlayFab._internalSettings.sessionTicket == null) throw \"Must be logged in to call this method\";\n";
    if (apiCall.auth === "SecretKey")
        return "        if (PlayFab.settings.developerSecretKey == null) throw \"Must have PlayFab.settings.developerSecretKey set to call this method\";\n";
    return "";
}

function HasResultActions(apiCall, api) {
    if (apiCall.result === "LoginResult" || apiCall.result === "RegisterPlayFabUserResult")
        return true;
    if (api.name === "Client" && apiCall.result === "AttributeInstallResult")
        return true;
    return false;
}

function GetResultActions(apiCall, api) {
    if (api.name === "Client" && (apiCall.result === "LoginResult" || apiCall.result === "RegisterPlayFabUserResult"))
        return "            if (result != null && result.data.SessionTicket != null) {\n" 
            + "                PlayFab._internalSettings.sessionTicket = result.data.SessionTicket;\n" 
            + "                PlayFab.ClientApi._MultiStepClientLogin(result.data.SettingsForUser.NeedsAttribution);\n" 
            + "            }";
    if (api.name === "Client" && apiCall.result === "AttributeInstallResult")
        return "            // Modify advertisingIdType:  Prevents us from sending the id multiple times, and allows automated tests to determine id was sent successfully\n" 
            + "            PlayFab.settings.advertisingIdType += \"_Successful\";\n";
    return "";
}

function GetUrl(apiCall, api) {
    return "PlayFab._internalSettings.GetServerUrl() + \"" + apiCall.url + "\"";
}

function GetAuthParams(apiCall) {
    if (apiCall.auth === "SecretKey")
        return "\"X-SecretKey\", PlayFab.settings.developerSecretKey";
    if (apiCall.auth === "SessionTicket")
        return "\"X-Authorization\", PlayFab._internalSettings.sessionTicket";
    return "null, null";
}

function GetDeprecationAttribute(tabbing, apiObj) {
    var isDeprecated = apiObj.hasOwnProperty("deprecation");
    
    if (isDeprecated && apiObj.deprecation.ReplacedBy != null)
        return tabbing + "/**\n" 
            + tabbing + " * @deprecated Please use " + apiObj.deprecation.ReplacedBy + " instead. \n" 
            + tabbing + " */\n";
    if (isDeprecated)
        return tabbing + "/**\n" 
            + tabbing + " * @deprecated Do not use\n" 
            + tabbing + " */\n";
    return "";
}

function GenerateSummary(tabbing, element, summaryParam, extraLine) {
    var hasSummary = element.hasOwnProperty(summaryParam);
    if (!hasSummary && !extraLine) {
        return "";
    }
    
    var output = tabbing + "/// <summary>\n";
    if (hasSummary)
        output += tabbing + "/// " + element[summaryParam] + "\n";
    if (extraLine)
        output += tabbing + "/// " + extraLine + "\n";
    output += tabbing + "/// </summary>\n";
    return output;
}

function GetBaseTypeSyntax(datatype) {
    if (datatype.className.toLowerCase().endsWith("request"))
        return " extends PlayFabApi.PlayFabRequestCommon";
    if (datatype.className.toLowerCase().endsWith("response") || datatype.className.toLowerCase().endsWith("result"))
        return " extends PlayFabApi.PlayFabResultCommon ";
    return ""; // If both are -1, then neither is greater
}

function GetPropertyTsType(property, datatype) {
    var output = undefined;
    
    if (property.actualtype === "String")
        output = "string";
    else if (property.actualtype === "Boolean")
        output = "boolean";
    else if (property.actualtype.contains("int") || property.actualtype === "float" || property.actualtype === "double" || property.actualtype === "decimal")
        output = "number";
    else if (property.actualtype === "DateTime")
        output = "string";
    else if (property.isclass)
        output = property.actualtype;
    else if (property.isenum)
        output = "string";
    else if (property.actualtype === "object")
        output = "any";
    else
        throw "Unknown property type: " + property.actualtype + " for " + property.name + " in " + datatype.className;
    
    if (property.collection === "array")
        output += "[]";
    else if (property.collection === "map")
        output = "{ [key: string]: " + output + " };";
    else if (property.collection)
        throw "Unknown collection type: " + property.collection + " for " + property.name + " in " + datatype.className;
    
    return output;
}
