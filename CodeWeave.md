# Refactoring Request: Move API Key Configuration to the Visual Studio Options Page

## Objective

Refactor the application so that the OpenAI API key is no longer hardcoded inside `Agent`, `AppHost`, or any application initialization code.

The API key should be configured through the Visual Studio **Options** page (`Options/OptionPage.cs`) and loaded at runtime.

The implementation must follow standard Visual Studio extension practices and ensure that sensitive configuration is centralized.

---

# Current Situation

The application currently contains a hardcoded API key.

Example:

```csharp
const string apiKey = "...";
ILLMClient client = new OpenAILLMClient(apiKey);
```

This is unacceptable because:

* Secrets are embedded in source code.
* The API key cannot be changed without recompiling.
* Different users cannot configure their own credentials.
* It violates basic security and deployment practices.

The API key must no longer exist anywhere in the source code.

---

# Desired Architecture

Configuration flow should become:

Visual Studio Options

Å´

OptionPage

Å´

Configuration Service

Å´

AppHost

Å´

OpenAILLMClient

The API key should flow only through configuration.

---

# OptionPage

Use the existing Visual Studio Options page.

`Options/OptionPage.cs`

Add a user-editable property:

```csharp
public string OpenAIApiKey { get; set; }
```

The property should be persisted using the standard Visual Studio options mechanism.

Do not implement custom configuration storage.

---

# Introduce a Configuration Service

Create a dedicated service responsible for retrieving extension configuration.

Example:

```csharp
public interface IConfigurationService
{
    string? OpenAIApiKey { get; }

    string ModelName { get; }

    TimeSpan ToolTimeout { get; }
}
```

Future configuration values should also be exposed through this service.

Avoid reading the OptionPage directly throughout the application.

---

# AppHost Responsibilities

`AppHost` should obtain configuration from the configuration service.

Example:

Configuration Service

Å´

Read API Key

Å´

Create OpenAILLMClient

Å´

Create AgentController

The application composition root should remain the only place where the LLM client is instantiated.

---

# Validation

Before constructing the LLM client:

* Verify that the API key is not null.
* Verify that it is not empty.
* Verify that it is not whitespace.

If validation fails:

* Do not construct the client.
* Report a meaningful error.
* Allow the application to continue running if possible.

The application should fail gracefully rather than crashing.

---

# Runtime Behavior

When the API key changes in the Visual Studio Options dialog:

Future implementations may recreate the LLM client.

For this refactoring, it is acceptable for changes to take effect after restarting the extension or Visual Studio.

Do not implement hot reloading unless it already exists.

---

# Security

Never:

* hardcode API keys
* log API keys
* display API keys in status messages
* include API keys in exception messages

Treat the API key as sensitive information throughout the application.

---

# Separation of Responsibilities

**OptionPage**

* Stores user-editable settings.

**ConfigurationService**

* Reads extension settings.
* Provides strongly typed configuration.

**AppHost**

* Consumes configuration.
* Creates application services.

**AgentController**

* Must never know where configuration originates.

**OpenAILLMClient**

* Receives the API key through constructor injection.

---

# Future Configuration

Design the configuration service so additional settings can be added easily.

Examples:

* Default model
* Temperature
* Tool timeout
* Startup session policy
* Permission policy
* Workspace restrictions
* Streaming enabled
* Logging level

No architectural changes should be required to support future options.

---

# Remove Legacy Code

Remove all hardcoded API key definitions.

No class should contain:

```csharp
const string apiKey = "...";
```

or equivalent code.

The application must compile and function correctly using only the configuration supplied through the Visual Studio Options page.

---

# Expected Outcome

After implementation:

* The API key is configured exclusively through the Visual Studio Options page.
* No secrets remain embedded in the source code.
* `AppHost` obtains configuration through a dedicated configuration service.
* `AgentController` and business logic remain independent of configuration storage.
* The architecture follows standard Visual Studio extension design practices and is ready for future configuration expansion.
