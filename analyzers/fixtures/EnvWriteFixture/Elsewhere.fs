module EnvWriteFixture.Elsewhere

/// The second writer, in both forms an access can take: the Fable macro and the CLR's own
/// method. A suite compiled for both runtimes writes through whichever it is running on, so a
/// rule that saw only one of them would see half of `Support`.

let plant (name: string) (value: string) =
    Access.set name value // YES007
    System.Environment.SetEnvironmentVariable (name, value) // YES007

/// Handing a child its own environment, and asking what this one holds. Neither writes.
let launch (run: obj) =
    Access.spawn run "C.UTF-8"
    System.Environment.GetEnvironmentVariable "LANG"
