# HomiliaMessenger Development Conventions

## Purpose and scope

These conventions apply to human contributors and AI coding assistants.
Prefer clear, simple implementations and deliberate contracts over
speculative abstractions.

HomiliaMessenger is a messenger-style desktop application for
experimenting with conversations with AI agents, where every contact is
an AI persona.

------------------------------------------------------------------------

## 1. General Design Philosophy

### Prefer Simple Designs

Follow KISS. Do not introduce abstractions, layers, patterns, or object
types solely to satisfy an architectural diagram. Every abstraction
should solve a concrete problem or protect an intentional boundary.

Avoid speculative architecture for capabilities the application does not
yet require.

### Enforce Separation of Concerns

Code should live with the responsibility it implements.

-   Presentation does not implement business logic.
-   Business does not implement persistence concerns.
-   Data access does not implement business validation.
-   External integration details do not leak into core business
concepts.
-   Persistence structure does not define business structure.
-   A project reference grants technical access, not architectural
    permission.

Only protocols, services, DTOs, and other types intentionally exposed
across a boundary should be consumed by another tier or capability.

### Preserve Locality of Behaviour

Within Business, organize code so behavior related to a capability is
easy to discover and reason about locally. Prefer cohesive
feature/domain organization over scattering related behavior throughout
large technical folders.

Reference: [Locality of
Behaviour](https://htmx.org/essays/locality-of-behaviour/)

### Prefer Modularity

The application should be modular both horizontally through
architectural boundaries and vertically through cohesive business
capabilities.

Modules should expose deliberate contracts rather than internal
implementation details.

------------------------------------------------------------------------

## 2. .NET Platform

Projects should target the current Long-Term Support version of .NET,
following the even-numbered .NET release cadence.

Prefer the highest supported even-numbered LTS release unless a concrete
compatibility constraint requires otherwise. Do not upgrade merely to
consume a non-LTS release without a compelling reason.

------------------------------------------------------------------------

## 3. Solution Architecture

Solution follows a modified three-tier architecture centered on
**Business**, with **Data** and **Integrations** treated as
implementation boundaries that satisfy application-facing contracts
defined by Business.

The familiar Data, Business, and Presentation responsibilities remain,
but dependencies across persistence and external-provider boundaries
should follow the **Dependency Inversion Principle**. Business defines
the capabilities the application requires; Data and Integrations
reference Business and implement those contracts.

A dedicated executable application project acts as the
**composition root**. It owns startup, dependency-injection composition,
application lifetime, and initialization that necessarily coordinates
concrete implementations. Presentation projects remain presentation
libraries rather than acquiring composition-root responsibilities merely
because they provide a user interface.

```text
                     Business

           ▲             ▲                ▲
           │             │                │
          Data      Integrations      Presentation
            ▲            ▲                ▲
             \           │               /
              \          │              /

            Executable - Composition Root
```

Conceptually:

```text
*.Business
    Defines business concepts, application behavior, validation,
    application-facing DTOs, and contracts for capabilities required
    from persistence and external providers.

*.Data
    Implements persistence capabilities required by Business and
    persists application-owned state.

*.Integrations
    Implements external capabilities required by Business by adapting
    third-party applications, standards, protocols, and services.

*.Presentation.[Desktop|Web|Android]
    Provides presentation-specific views, state, binding models, and
    interaction behavior over intentionally exposed Business
    capabilities. CLI applications typically would not include this
    project.

*.[DesktopApp|WebApp|AndroidApp|CLIApp]
    Is the executable and composition root for the desktop application.
    It references the projects required to assemble the application,
    registers concrete implementations, performs application startup and
    initialization, and owns the application lifetime.
```

Business remains the architectural center. The composition root has
broad technical visibility because assembling the application is its
responsibility; that visibility does not change the architectural
ownership of Business, Data, Integrations, or Presentation behavior.
Technical visibility does not grant architectural permission to consume
an implementation type outside the responsibility that justified the
reference.

------------------------------------------------------------------------

## 4. Presentation Patterns

### Desktop and Mobile GUI

Cross-platform desktop and mobile GUI applications follow **MVVM**.

``` text
View
    Visual structure and presentation behavior.

ViewModel
    Presentation state, commands, and coordination.

Model
    Presentation-facing data suitable for binding.
```

ViewModels should not depend on Views or UI-framework types. Views
should not locate or construct their own ViewModels. Presentation
composition establishes the root ViewModel, while child ViewModels
should be supplied through presentation-owned content or activation
mechanisms.

Content identity, content activation, and content placement are separate
presentation concerns. Dependency injection may construct presentation
objects, but DI resolution should not define navigation semantics or
determine whether content appears in the current shell region, a tab, a
docked panel, a separate window, or another presentation form.

Do not introduce generalized navigation or workspace abstractions until
concrete interaction requirements justify them.

### WinForms

Follows a lightweight **Model-View-Presenter (MVP)** pattern. Forms and
controls are views: they expose user actions and display state, but
avoid owning application behavior. Presenters respond to view events,
coordinate business services, and update the view through interfaces.
Keep domain and persistence logic out of the presentation layer, and
keep views free of direct dependencies on concrete data or integration
implementations.

### Web

Use a pattern appropriate to the chosen web technology while maintaining
the same separation-of-concerns rules.

### CLI

HomiliaMessenger currently has no CLI host. Design shared capabilities
so such a host could reuse them unchanged.

Formal pattern is not required for terminal applications. Prefer the
simplest pattern suited to command-line interaction, for example:

``` text
Command
    ↓
Business service/process
    ↓
Result
    ↓
Console rendering
```

The absence of a formal pattern does not relax architectural separation.

------------------------------------------------------------------------

## 5. Pattern Naming Conventions

Use architectural suffixes only when a class actually performs that
role.

-   View classes end in `View`.
-   Presenter classes end in `Presenter`.
-   ViewModel classes end in `ViewModel`.
-   Controller classes end in `Controller`.
-   Classes that do not perform these roles must not use these suffixes.

------------------------------------------------------------------------

## 6. Business Object Vocabulary

### Persistent Entities / Data Entities

A **persistent entity** or **data entity** is a uniquely identifiable
persisted data representation. Use these qualified terms in
architectural discussion when ambiguity is possible.

Do not call every business object an entity merely because it has
identity.

Persistence structure does not define business structure. Multiple data
entities may support one business concept, and a data entity may exist
only for caching, synchronization, indexing, auditing, integration
state, or other infrastructure bookkeeping.

Persistent entities should generally remain simple data-oriented
objects.

### DTOs

DTOs are lightweight objects intended to transfer data across an
intentional boundary.

DTOs should:

-   Be serializable by design.
-   Be POCOs.
-   Generally be C# records.
-   Contain data rather than complex behavior.
-   Represent intended contract exposure rather than blindly mirror
persistence or provider representations.

Example:

``` csharp
public sealed record FooDto(
    string Name,
    string? Description,
    Guid FooId);
```

External integrations may have provider-specific DTOs in addition to
app-facing DTOs. Do not expose an external provider DTO directly merely
to avoid writing a mapping.

### Domains / Business Concepts

A **domain** or **business concept** represents a real-world or
app-specific concept required by business logic. It is not defined by
whether or how it persists.

A business representation may have one persistent representation,
multiple persistent representations, or none.

Business objects should generally remain straightforward data-oriented
representations. Prefer focused business services/processes for
non-trivial behavior rather than automatically placing complex behavior
on the objects themselves.

Useful DDD ideas may be adopted when they solve a concrete problem, but
DDD terminology or rich domain objects are not required by default.

### Models

A **Model** is a representation intended for presentation consumption.
Models may represent a view of business concepts, DTOs, or composed
information from multiple sources.

In MVVM and MVC, Models are suitable for presentation consumption and
data binding. Presentation Models belong to Presentation when their
shape exists specifically for UI needs.

### Do Not Manufacture One-to-One Representations

Do not automatically create all of the following for every concept:

``` text
FooEntity
Foo
FooDto
FooModel
```

Create a distinct representation only when a real boundary or
transformation requires it. KISS takes precedence over architectural
ceremony.

------------------------------------------------------------------------

## 7. Business Behavior

### Prefer Focused Processes and Services

Generally favor restrained behavior on data-oriented objects.
Non-trivial operations should normally live in focused
services/processes near the capability they implement.

For example:

``` text
Goals/
├── Goal.cs
├── Objective.cs
├── CreateGoalProcess.cs
├── AddObjectiveProcess.cs
└── CompleteGoalProcess.cs
```

Exact organization should follow the capability and Locality of
Behaviour rather than a mandatory folder template.

### Validation

Application DTOs should be validated at the appropriate
business/application boundary.

Do not put business validation in Data, depend on UI validation as sole
enforcement, or treat database constraints as a replacement for business
validation. Database constraints may still enforce data integrity
defensively.

------------------------------------------------------------------------

## 8. Integration Contracts

Use the following terminology consistently:

-   **Provider** — an implementation the app knows how to connect to.
-   **Integration** — a user-configured instance of a Provider.
-   **Capability** — an application operation or family of operations
    that a Provider can satisfy.

> **Providers satisfy capabilities. Integrations configure Providers.
> Presentations, automations, and agents consume capabilities.**

Business defines application-facing capability contracts; Integrations
implements them. Introduce typed capability contracts from concrete
requirements rather than creating speculative interfaces for every
possible external-system category.

Provider-specific types remain inside the corresponding integration
boundary. App-facing contracts use app-defined DTOs and business
concepts.

Secrets must not be stored in provider configuration. Persist only an
opaque credential reference and resolve secret material through the
appropriate infrastructure boundary when a real Provider requires it.

Read and write authority should be distinguishable when concrete
capabilities require it, particularly where agents or automation may
exercise those capabilities.

------------------------------------------------------------------------

## 9. Expected Failures and Exceptions

Expected operational states should generally be represented explicitly
rather than communicated through exceptions, especially for
integrations.

Examples:

``` text
Success
NotFound
Unavailable
Unauthorized
Forbidden
InvalidResponse
```

A result type may be appropriate. Use exceptions for exceptional or
unexpected failures rather than normal control flow.

In HomiliaMessenger, Business validation is implemented with
FluentValidation, and operations whose input may be invalid return the
`ValidationResult` rather than throwing. Introduce a capability result
type for provider outcomes (for example, an agent being unavailable)
when the first real provider requires one.

Reference: [Exception-Handling Antipatterns and API
Design](https://www.infoq.com/articles/Exceptions-API-Design/)

------------------------------------------------------------------------

## 10. C# Coding Conventions

### Namespaces

Prefer file-scoped namespaces.

``` csharp
namespace MyApp.Business.Goals;
```

### Local Variable Declarations

Prefer an explicit type with target-typed construction:

``` csharp
Foo foo = new();
```

over:

``` csharp
var foo = new Foo();
```

Use judgment where explicit typing would make code substantially less
readable.

### Constants

Name constants using uppercase snake case.

``` csharp
private const string API_ENDPOINT = "https://rest.example.com/v1";
```

### Private Fields

Use camel case with a leading underscore.

``` csharp
private int _counter;
```

### Public and Internal Members

Use PascalCase.

``` csharp
public required DateTime TimeStamp { get; set; }
```

### Parameters

- Use ordinary constructors for classes rather than primary constructors.
- Very simple POCOs, DTOs, and records may use primary constructors.
- Keep parameter lists on one line where readable.

### Bodies
- **Always use braces for `if`, `else`, loops, and `lock`, even when the  body contains only one statement.**
- Put braces on separate lines (Allman style). Expand method, constructor,
  operator, accessor, try/catch/finally, and lambda blocks. Put each executable
  statement on its own line; never compress several statements into one line.
- Methods, constructors, operators, and local functions use block bodies.
  Simple expression-bodied properties/indexers/accessors are acceptable. Auto-properties
  may remain on one line because they contain no executable statements.

### General C# Style

- Use four spaces, LF line endings, UTF-8, a final newline, and no trailing whitespace.
- Use `string.Empty` for an empty string.

Except where scoped definitions include more specific conventions,
generally follow
[Microsoft C# Coding Conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).

### HTTP Clients

Prefer `IHttpClientFactory` for application and integration code that
performs HTTP requests. Register HTTP client configuration in the
executable composition root and inject `IHttpClientFactory` into
long-lived consumers rather than registering or capturing an
unconfigured singleton `HttpClient`.

Create clients when an operation needs them:

``` csharp
HttpClient httpClient = _httpClientFactory.CreateClient();
```

Creating an `HttpClient` through the factory is inexpensive because the
factory manages and pools the underlying handlers. Named or typed
clients may be used when a concrete integration needs stable
provider-specific HTTP configuration.

A long-lived `HttpClient` is not inherently incorrect. It can be
appropriate when deliberately configured with connection-lifetime
management such as `SocketsHttpHandler.PooledConnectionLifetime`. Prefer
that approach only when its lifetime and handler policy are intentional
and provide a concrete advantage over the application's existing
`IHttpClientFactory` infrastructure.

Do not create a separate HTTP lifetime policy inside an integration
merely because it needs HTTP access. The composition root owns
application-level HTTP infrastructure; integration code owns
provider/protocol behavior.

### Library Design

Libraries should generally follow the [.NET Framework Design
Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)
where applicable.

------------------------------------------------------------------------

## 11. Contributor Coding Guidance

When adding or modifying code:

1.  Preserve Data, Business, Integration, and Presentation boundaries.
2.  Do not introduce provider-specific types into Business.
3.  Do not introduce persistent/data entities into Presentation.
4.  Do not implement business validation in Data.
5.  Do not implement business logic in Presentation.
6.  Prefer focused capability-local services/processes over large
    generic managers.
7.  Prefer simple data-oriented business objects unless object behavior
    provides a clear advantage.
8.  Use records for DTOs unless there is a concrete reason not to.
9.  Do not create DTOs, Models, data entities, interfaces, repositories,
    or mappings merely for architectural symmetry.
10. Prefer explicit contracts at project boundaries.
11. Preserve external systems as canonical owners of information the app
does not intrinsically own.
12. Prefer open protocols and provider abstractions over vendor
    coupling.
13. Follow this document's C# naming and formatting conventions.
14. Ask whether an abstraction solves a current problem before adding
    it.
15. If a convention appears inappropriate for a concrete problem,
    explain the tradeoff rather than silently working around it.
16. Give appropriate credit when implementation or design is substantially
    derived from an identifiable external source rather than merely informed
    by general knowledge. When practical, cite the source near the adopted
    implementation, typically in XML documentation such as a `<remarks>`
    element. This applies even to permissively licensed, public-domain, CC0,
    educational, textbook, or article sources. Use judgment: ordinary
    techniques and broadly learned ideas do not require mechanical citations.

### Decision Heuristics

When deciding where new code belongs:

**Is this about storing app-owned information?**\
It probably belongs in `Data`.

**Is this a rule, concept, process, validation, or capability
contract?**\
It probably belongs in `Business`.

**Does it understand an external application's API, protocol, or
representation?**\
It probably belongs in `Integrations.<Provider>`.

**Does its shape or behavior exist specifically because of a user
interface?**\
It probably belongs in `Presentation.<Platform>`.

**Does a new class merely duplicate another representation without
protecting a boundary?**\
Do not create it yet.

**Does a database table exist only for infrastructure reasons?**\
Do not invent a matching business concept.

**Does a business concept persist?**\
That does not make the persistence representation the business model.

------------------------------------------------------------------------

## 12. Guiding Architectural Rule

> **Architecture should make intended behavior easy to find, intended
> dependencies obvious, and unintended coupling difficult.**

Favor explicit boundaries and consistent conventions, but simplicity and
clear responsibility take precedence over pattern compliance for its own
sake.
