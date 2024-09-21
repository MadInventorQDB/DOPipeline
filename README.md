
# DOPipeline

![Build Status](https://img.shields.io/github/actions/workflow/status/johna/DOPipeline/ci.yml?branch=main&label=Build) ![License](https://img.shields.io/github/license/johna/DOPipeline)

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Architecture](#architecture)
  - [Entity-Component-System (ECS) Pattern](#entity-component-system-ecs-pattern)
  - [Pipeline Structure](#pipeline-structure)
- [Installation](#installation)
- [Usage](#usage)
- [DifferentialBackup Demonstration](#differentialbackup-demonstration)
  - [Backup](#backup)
  - [Restore](#restore)
  - [Query](#query)
- [Project Structure](#project-structure)
- [Testing](#testing)
- [Contributing](#contributing)
- [License](#license)

## Overview

**DOPipeline** is a robust and flexible data-oriented pipeline framework developed in C#. It leverages the **Entity-Component-System (ECS)** architectural pattern to provide a modular and scalable approach to processing data through a series of systems. This design promotes separation of concerns, reusability, and ease of maintenance.

**DifferentialBackup** is a demonstration project built on top of DOPipeline. It showcases the framework's capabilities by implementing a command-line utility that performs efficient differential backups, allowing users to back up, restore, and query backup states with ease.

## Features

### DOPipeline

- **Modular Architecture:** Easily add, remove, or modify systems within the pipeline without affecting other components.
- **Entity-Component-System (ECS) Design:** Promotes separation of data (components) from behavior (systems), enhancing flexibility and reusability.
- **Pipeline Configuration:** Organize systems into sequential execution stages (pipes) for streamlined data processing.
- **Robust Error Handling:** Gracefully handles failures within pipeline execution, ensuring reliability.
- **Comprehensive Testing:** Includes extensive unit tests to ensure framework stability and correctness.

### DifferentialBackup

- **Backup:** Perform differential backups by only copying changed files, optimizing storage and time.
- **Restore:** Restore files from a specific backup date, ensuring data integrity and availability.
- **Query:** List all available backup dates for easy management and selection.
- **Interactive Prompts:** User-friendly prompts for required inputs when command-line arguments are not provided.
- **Data Persistence:** Maintains file hashes and backup dates for efficient backup operations.

## Architecture

### Entity-Component-System (ECS) Pattern

DOPipeline is built upon the **Entity-Component-System (ECS)** architectural pattern, which decouples data from behavior, allowing for flexible and efficient data processing.

- **Entities:** Unique identifiers that represent objects or concepts within the system. They do not contain data themselves but can have multiple components attached to them.
  
  ```csharp
  public class Entity
  {
      public Guid Id { get; } = Guid.NewGuid();
      // Equality based on unique ID
  }
  ```

- **Components:** Data containers that hold specific pieces of information. Components are attached to entities to define their data.
  
  ```csharp
  public class FilePathComponent : IComponent
  {
      public string FilePath { get; set; } = string.Empty;
  }
  ```

- **Systems:** Logic that operates on entities with specific components. Systems perform actions or computations based on the data provided by components.
  
  ```csharp
  public class FileDiscoverySystem : ISystem
  {
      public Result Execute(Entity entity, IComponentStorage storage)
      {
          // Implementation
      }
  }
  ```

### Pipeline Structure

DOPipeline organizes systems into sequential execution stages called **pipes**. Each pipe contains one or more systems that process entities in a defined order.

1. **Pipeline:** The overarching structure that contains multiple pipes.
2. **Pipe:** A single execution stage within the pipeline, containing a sequence of systems.
3. **System:** Individual units of logic that perform specific tasks on entities.

This structure allows for clear organization and easy modification of the data processing flow.

```csharp
var pipelineBuilder = new PipelineBuilder();

pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
    .Named("File Discovery")
    .AddSystem(new FileDiscoverySystem(sourceDirectory)));

pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
    .Named("Hash Calculation")
    .AddSystem(new HashCalculationSystem(fileHashes)));

// Build the pipeline
var pipeline = pipelineBuilder.Build();
```

## Installation

### Prerequisites

- [.NET 6.0 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) or later installed on your machine.

### Clone the Repository

```bash
git clone https://github.com/johna/DOPipeline.git
cd DOPipeline
```

### Build the Solution

Navigate to the solution directory and build the projects:

```bash
dotnet build
```

## Usage

DOPipeline serves as a foundational framework for building data processing pipelines using the ECS pattern. To demonstrate its capabilities, the `DifferentialBackup` project provides a practical application.

### DifferentialBackup Demonstration

The `DifferentialBackup` utility showcases how DOPipeline can be used to implement a functional and efficient backup system.

#### Backup

Perform a backup by specifying the source directory and backup destination.

##### Command

```bash
DifferentialBackup.exe backup <SourceDirectory> <BackupDestination>
```

##### Example

```bash
DifferentialBackup.exe backup "C:\Users\johna\Documents\Source" "D:\Backups\SourceBackup"
```

If arguments are not provided, the utility will prompt for the required directories interactively.

#### Restore

Restore files from a specific backup date.

##### Command

```bash
DifferentialBackup.exe restore <BackupDestination> <RestoreDestination>
```

##### Example

```bash
DifferentialBackup.exe restore "D:\Backups\SourceBackup" "C:\Users\johna\Documents\Restore"
```

The utility will automatically select the latest backup date unless specified otherwise.

#### Query

List all available backup dates for a given backup destination.

##### Command

```bash
DifferentialBackup.exe query <BackupDestination>
```

##### Example

```bash
DifferentialBackup.exe query "D:\Backups\SourceBackup"
```

#### Help

Display help information about the utility.

##### Command

```bash
DifferentialBackup.exe help
```

## Project Structure

```
DOPipeline/
├── DifferentialBackup/
│   ├── Components/
│   ├── Pipeline/
│   ├── Systems/
│   ├── Utilities/
│   ├── DifferentialBackup.csproj
│   └── Program.cs
├── DifferentialBackup.Test/
│   ├── Pipeline/
│   ├── Systems/
│   ├── Utilities/
│   ├── DifferentialBackup.Test.csproj
│   └── AssemblyInfo.cs
├── DOPipeline/
│   ├── Builders/
│   ├── Components/
│   ├── Entities/
│   ├── Pipeline/
│   ├── Storage/
│   ├── Systems/
│   ├── Utilities/
│   ├── DOPipeline.csproj
│   └── ...
├── DOPipeline.Test/
│   ├── Builders/
│   ├── Components/
│   ├── Entities/
│   ├── Pipeline/
│   ├── Storage/
│   ├── Systems/
│   ├── Utilities/
│   ├── DOPipeline.Test.csproj
│   └── ...
├── .gitignore
├── .gitattributes
└── DOPipeline.sln
```

### Key Directories

- **DOPipeline:** Core pipeline framework implementing the ECS pattern.
- **DOPipeline.Test:** Unit tests for the DOPipeline framework.
- **DifferentialBackup:** Demonstration project utilizing DOPipeline to perform backup operations.
- **DifferentialBackup.Test:** Unit tests for the DifferentialBackup project.

## Testing

Both DOPipeline and DifferentialBackup include comprehensive unit tests to ensure reliability and correctness.

### Running Tests

Navigate to the solution directory and execute:

```bash
dotnet test
```

This command will build and run all tests in both `DOPipeline.Test` and `DifferentialBackup.Test` projects.

## Contributing

Contributions are welcome! Please follow these steps to contribute:

1. **Fork the Repository**

2. **Create a Feature Branch**

   ```bash
   git checkout -b feature/YourFeature
   ```

3. **Commit Your Changes**

   ```bash
   git commit -m "Add some feature"
   ```

4. **Push to the Branch**

   ```bash
   git push origin feature/YourFeature
   ```

5. **Open a Pull Request**

Please ensure your code follows the existing style and includes appropriate tests.

## License

This project is licensed under the [MIT License](LICENSE).

---

**Note:** Replace the badge URLs with actual links to your GitHub Actions workflows and license file as needed.

## ECS Usage Examples

To illustrate how the **Entity-Component-System (ECS)** pattern is utilized within DOPipeline, here are simple examples demonstrating the creation and processing of entities with components through systems.

### Example 1: File Discovery

**Components Involved:**

- `FilePathComponent`: Holds the file path information.

**System:**

- `FileDiscoverySystem`: Discovers all files in a specified directory and attaches `FilePathComponent` to new entities.

**Workflow:**

1. **Entity Creation:** A new entity is created to represent the source directory.
2. **System Execution:** `FileDiscoverySystem` scans the directory, creates new entities for each file found, and attaches `FilePathComponent` with the respective file paths.

```csharp
// Initialize storage and create initial entity
var storage = new ComponentStorage();
var initialEntity = new Entity();
storage.SetComponent(initialEntity, new FilePathComponent { FilePath = sourceDirectory });
var entities = new List<Entity> { initialEntity };

// Build and execute the pipeline
var backupPipeline = BackupPipelineBuilder.BuildBackupPipeline(sourceDirectory, backupDestination, fileHashes, backupDates);
var backupResult = backupPipeline.Execute(entities, storage);
```

### Example 2: Hash Calculation

**Components Involved:**

- `FileHashComponent`: Stores the current and previous SHA-256 hashes of a file.

**System:**

- `HashCalculationSystem`: Computes the SHA-256 hash of each file and updates `FileHashComponent`.

**Workflow:**

1. **Entity Processing:** For each entity with a `FilePathComponent`, `HashCalculationSystem` computes the current hash.
2. **Component Update:** Updates `FileHashComponent` with the new hash and retains the previous hash for comparison.

```csharp
public class HashCalculationSystem : ISystem
{
    public Result Execute(Entity entity, IComponentStorage storage)
    {
        var filePathComponent = storage.GetComponent<FilePathComponent>(entity);
        if (filePathComponent == null)
            return Result.Fail("FilePathComponent missing.");

        var currentHash = HashUtility.ComputeSHA256(filePathComponent.FilePath);
        if (currentHash == null)
            return Result.Fail("Failed to compute hash.");

        var fileHashComponent = new FileHashComponent
        {
            CurrentHash = currentHash,
            PreviousHash = fileHashes.ContainsKey(filePathComponent.FilePath) ? fileHashes[filePathComponent.FilePath] : string.Empty
        };

        storage.SetComponent(entity, fileHashComponent);
        return Result.Success();
    }
}
```

### Example 3: Backup Decision

**Components Involved:**

- `FileHashComponent`: Used to determine if a file has changed.
- `BackupDateComponent`: Marks an entity for backup if changes are detected.

**System:**

- `BackupDecisionSystem`: Compares current and previous hashes to decide if a file needs to be backed up.

**Workflow:**

1. **Hash Comparison:** For each entity, `BackupDecisionSystem` checks if the `CurrentHash` differs from the `PreviousHash`.
2. **Backup Marking:** If a change is detected, attaches `BackupDateComponent` with the current timestamp.
3. **Hash Update:** Updates the stored hash for future comparisons.

```csharp
public class BackupDecisionSystem : ISystem
{
    public Result Execute(Entity entity, IComponentStorage storage)
    {
        var fileHashComponent = storage.GetComponent<FileHashComponent>(entity);
        var filePathComponent = storage.GetComponent<FilePathComponent>(entity);

        if (fileHashComponent == null || filePathComponent == null)
            return Result.Fail("Required components missing.");

        if (fileHashComponent.PreviousHash != fileHashComponent.CurrentHash)
        {
            // Mark the entity for backup
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = DateTime.UtcNow });

            // Update the hash in storage
            fileHashes[filePathComponent.FilePath] = fileHashComponent.CurrentHash;

            // Record the backup date
            backupDates.Add(DateTime.UtcNow);
        }

        return Result.Success();
    }
}
```

These examples demonstrate how DOPipeline leverages the ECS pattern to efficiently manage and process data through various systems. By decoupling data from behavior, DOPipeline ensures that each system remains focused on a single responsibility, enhancing maintainability and scalability.
