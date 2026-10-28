# Third-party notices

CPAMP–BrimDeck Bridge source is provided under the MIT license in LICENSE.

The self-contained Windows executable bundles Microsoft .NET 8, Windows Forms, and ASP.NET Core runtime components. Their applicable licenses and third-party notices are available at [dotnet/runtime](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt). The distribution includes the runtime THIRD-PARTY-NOTICES.TXT when packaged.

The application uses Windows-supplied winsqlite3.dll and Windows DPAPI; no copy of the Windows DLLs is distributed.

The bridge interoperates with [CPA-Manager-Plus](https://github.com/seakee/CPA-Manager-Plus) and [BrimDeck](https://github.com/MoFeng2223/BrimDeck). Their source and binaries are not included in this project's release. This is an independent integration project, not an official release of either application.

Optional BrimDeck compatibility tests reference a user's existing BrimDeck.Core, Jint, Acornima and ZstdSharp assemblies in place. These are not part of the bridge executable or release archive. The repository's fixtures are synthetic; published numerical integration evidence contains no account names or credentials.
