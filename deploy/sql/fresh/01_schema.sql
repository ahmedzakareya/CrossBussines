SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AccountingSettings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AccountingSettings](
	[CompanyID] [int] NOT NULL,
	[ApprovalThreshold] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Accountin__Appro__07220AB2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AccountingSettings] ADD  DEFAULT ((0)) FOR [ApprovalThreshold]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AccountingUserRoles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AccountingUserRoles](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeId] [int] NOT NULL,
	[Role] [nvarchar](40) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Accounts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Accounts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NOT NULL,
	[AccountTypeId] [int] NOT NULL,
	[ParentId] [int] NULL,
	[IsPostable] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CurrencyId] [int] NULL,
	[RequireCostCenter] [bit] NOT NULL,
	[RequireProject] [bit] NOT NULL,
	[CashFlowCategory] [nvarchar](20) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ModifiedBy] [nvarchar](450) NULL,
	[ModifiedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Accounts]') AND name = N'UX_Accounts_Company_Code')
CREATE UNIQUE NONCLUSTERED INDEX [UX_Accounts_Company_Code] ON [dbo].[Accounts]
(
	[CompanyID] ASC,
	[Code] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Accounts__IsPost__531856C7]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Accounts] ADD  DEFAULT ((0)) FOR [IsPostable]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Accounts__IsActi__540C7B00]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Accounts] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Accounts__Requir__55009F39]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Accounts] ADD  DEFAULT ((0)) FOR [RequireCostCenter]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Accounts__Requir__55F4C372]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Accounts] ADD  DEFAULT ((0)) FOR [RequireProject]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_PADDING ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AccountTypes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AccountTypes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[Code] [nvarchar](20) NOT NULL,
	[Name] [nvarchar](100) NOT NULL,
	[NameEn] [nvarchar](100) NOT NULL,
	[NormalBalance] [char](1) NOT NULL,
	[StatementType] [nvarchar](20) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING OFF
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Activities]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Activities](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Type] [nvarchar](40) NOT NULL,
	[Subject] [nvarchar](300) NOT NULL,
	[DueDate] [datetime2](7) NULL,
	[Done] [bit] NOT NULL,
	[LeadId] [int] NULL,
	[OpportunityId] [int] NULL,
	[CustomerId] [int] NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[OwnerEmployeeId] [int] NULL,
	[EntityType] [nvarchar](20) NULL,
	[EntityId] [int] NULL,
	[ReminderAt] [datetime2](7) NULL,
	[Reminded] [bit] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Activities]') AND name = N'IX_Activities_Company')
CREATE NONCLUSTERED INDEX [IX_Activities_Company] ON [dbo].[Activities]
(
	[CompanyID] ASC,
	[Done] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Activities]') AND name = N'IX_Activities_Entity')
CREATE NONCLUSTERED INDEX [IX_Activities_Entity] ON [dbo].[Activities]
(
	[CompanyID] ASC,
	[EntityType] ASC,
	[EntityId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Activities]') AND name = N'IX_Activities_Reminder')
CREATE NONCLUSTERED INDEX [IX_Activities_Reminder] ON [dbo].[Activities]
(
	[CompanyID] ASC,
	[Reminded] ASC,
	[ReminderAt] ASC
)
WHERE ([ReminderAt] IS NOT NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Activities__Type__5B0E7E4A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Activities] ADD  DEFAULT ('Task') FOR [Type]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Activities__Done__5C02A283]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Activities] ADD  DEFAULT ((0)) FOR [Done]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_Activities_Reminded]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Activities] ADD  CONSTRAINT [DF_Activities_Reminded]  DEFAULT ((0)) FOR [Reminded]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AdministrativeBodiesCompanies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AdministrativeBodiesCompanies](
	[A_ID] [int] IDENTITY(1,1) NOT NULL,
	[NameAr] [nvarchar](max) NOT NULL,
	[NameEN] [nvarchar](max) NOT NULL,
	[Notes] [nvarchar](max) NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
 CONSTRAINT [PK_AdministrativeBodiesCompanies] PRIMARY KEY CLUSTERED 
(
	[A_ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AiInteractions]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AiInteractions](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[UserId] [nvarchar](450) NULL,
	[Feature] [nvarchar](60) NOT NULL,
	[InputText] [nvarchar](max) NULL,
	[OutputJson] [nvarchar](max) NULL,
	[Model] [nvarchar](60) NULL,
	[InputTokens] [int] NULL,
	[OutputTokens] [int] NULL,
	[Confidence] [decimal](5, 4) NULL,
	[UserDecision] [nvarchar](20) NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_PADDING ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AiKnowledgeChunks]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AiKnowledgeChunks](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[Source] [nvarchar](200) NOT NULL,
	[Lang] [char](2) NOT NULL,
	[Content] [nvarchar](max) NOT NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING OFF
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AiSuggestions]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AiSuggestions](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Feature] [nvarchar](60) NOT NULL,
	[TargetType] [nvarchar](40) NULL,
	[TargetId] [int] NULL,
	[PayloadJson] [nvarchar](max) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AiSuggestions_Status]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AiSuggestions] ADD  CONSTRAINT [DF_AiSuggestions_Status]  DEFAULT ('Pending') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_PADDING ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AppCache]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AppCache](
	[Id] [nvarchar](449) NOT NULL,
	[Value] [varbinary](max) NOT NULL,
	[ExpiresAtTime] [datetimeoffset](7) NOT NULL,
	[SlidingExpirationInSeconds] [bigint] NULL,
	[AbsoluteExpiration] [datetimeoffset](7) NULL,
 CONSTRAINT [pk_AppCache_Id] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING OFF
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AppCache]') AND name = N'Index_AppCache_ExpiresAtTime')
CREATE NONCLUSTERED INDEX [Index_AppCache_ExpiresAtTime] ON [dbo].[AppCache]
(
	[ExpiresAtTime] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetRoleClaims]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetRoleClaims](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[RoleId] [nvarchar](450) NOT NULL,
	[ClaimType] [nvarchar](max) NULL,
	[ClaimValue] [nvarchar](max) NULL,
 CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetRoleClaims]') AND name = N'IX_AspNetRoleClaims_RoleId')
CREATE NONCLUSTERED INDEX [IX_AspNetRoleClaims_RoleId] ON [dbo].[AspNetRoleClaims]
(
	[RoleId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetRoles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetRoles](
	[Id] [nvarchar](450) NOT NULL,
	[Name] [nvarchar](256) NULL,
	[NormalizedName] [nvarchar](256) NULL,
	[ConcurrencyStamp] [nvarchar](max) NULL,
 CONSTRAINT [PK_AspNetRoles] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetRoles]') AND name = N'RoleNameIndex')
CREATE UNIQUE NONCLUSTERED INDEX [RoleNameIndex] ON [dbo].[AspNetRoles]
(
	[NormalizedName] ASC
)
WHERE ([NormalizedName] IS NOT NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserClaims]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetUserClaims](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[UserId] [nvarchar](450) NOT NULL,
	[ClaimType] [nvarchar](max) NULL,
	[ClaimValue] [nvarchar](max) NULL,
 CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserClaims]') AND name = N'IX_AspNetUserClaims_UserId')
CREATE NONCLUSTERED INDEX [IX_AspNetUserClaims_UserId] ON [dbo].[AspNetUserClaims]
(
	[UserId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserLogins]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetUserLogins](
	[LoginProvider] [nvarchar](450) NOT NULL,
	[ProviderKey] [nvarchar](450) NOT NULL,
	[ProviderDisplayName] [nvarchar](max) NULL,
	[UserId] [nvarchar](450) NOT NULL,
 CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY CLUSTERED 
(
	[LoginProvider] ASC,
	[ProviderKey] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserLogins]') AND name = N'IX_AspNetUserLogins_UserId')
CREATE NONCLUSTERED INDEX [IX_AspNetUserLogins_UserId] ON [dbo].[AspNetUserLogins]
(
	[UserId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetUserRoles](
	[UserId] [nvarchar](450) NOT NULL,
	[RoleId] [nvarchar](450) NOT NULL,
 CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY CLUSTERED 
(
	[UserId] ASC,
	[RoleId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]') AND name = N'IX_AspNetUserRoles_RoleId')
CREATE NONCLUSTERED INDEX [IX_AspNetUserRoles_RoleId] ON [dbo].[AspNetUserRoles]
(
	[RoleId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUsers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetUsers](
	[Id] [nvarchar](450) NOT NULL,
	[IsEndUser] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[UserName] [nvarchar](256) NULL,
	[NormalizedUserName] [nvarchar](256) NULL,
	[Email] [nvarchar](256) NULL,
	[NormalizedEmail] [nvarchar](256) NULL,
	[EmailConfirmed] [bit] NOT NULL,
	[PasswordHash] [nvarchar](max) NULL,
	[SecurityStamp] [nvarchar](max) NULL,
	[ConcurrencyStamp] [nvarchar](max) NULL,
	[PhoneNumber] [nvarchar](max) NULL,
	[PhoneNumberConfirmed] [bit] NOT NULL,
	[TwoFactorEnabled] [bit] NOT NULL,
	[LockoutEnd] [datetimeoffset](7) NULL,
	[LockoutEnabled] [bit] NOT NULL,
	[AccessFailedCount] [int] NOT NULL,
 CONSTRAINT [PK_AspNetUsers] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUsers]') AND name = N'EmailIndex')
CREATE NONCLUSTERED INDEX [EmailIndex] ON [dbo].[AspNetUsers]
(
	[NormalizedEmail] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUsers]') AND name = N'UserNameIndex')
CREATE UNIQUE NONCLUSTERED INDEX [UserNameIndex] ON [dbo].[AspNetUsers]
(
	[NormalizedUserName] ASC
)
WHERE ([NormalizedUserName] IS NOT NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AspNetUserTokens]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AspNetUserTokens](
	[UserId] [nvarchar](450) NOT NULL,
	[LoginProvider] [nvarchar](450) NOT NULL,
	[Name] [nvarchar](450) NOT NULL,
	[Value] [nvarchar](max) NULL,
 CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY CLUSTERED 
(
	[UserId] ASC,
	[LoginProvider] ASC,
	[Name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AssetCategories]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AssetCategories](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[DefaultUsefulLifeMonths] [int] NOT NULL,
	[DepreciationMethod] [nvarchar](30) NOT NULL,
	[CostAccountId] [int] NOT NULL,
	[AccumDepAccountId] [int] NOT NULL,
	[DepExpenseAccountId] [int] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__AssetCate__Defau__29E1370A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AssetCategories] ADD  DEFAULT ((60)) FOR [DefaultUsefulLifeMonths]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__AssetCate__Depre__2AD55B43]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AssetCategories] ADD  DEFAULT ('StraightLine') FOR [DepreciationMethod]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__AssetCate__IsAct__2BC97F7C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AssetCategories] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Attachments]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Attachments](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[AttachName] [nvarchar](max) NOT NULL,
	[attachPath] [nvarchar](max) NOT NULL,
	[FormID] [int] NOT NULL,
	[RequestID] [int] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[EmployeeID] [int] NULL,
 CONSTRAINT [PK_Attachments] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AttendancePolicies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AttendancePolicies](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LeavePolicyTypeID] [int] NOT NULL,
	[AllowedGraceMinutes] [int] NOT NULL,
	[WarningThresholdCount] [int] NOT NULL,
	[DeductionRatePerOccurrence] [decimal](19, 4) NOT NULL,
	[UnexcusedAbsenceThreshold] [int] NOT NULL,
	[WorkStartTime] [time](7) NULL,
	[WorkEndTime] [time](7) NULL,
	[WorkHoursPerDay] [decimal](4, 2) NULL,
	[BreakStartTime] [time](7) NULL,
	[BreakEndTime] [time](7) NULL,
	[BreakDurationMinutes] [int] NULL,
	[WorkDaysPerWeek] [int] NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[WorkOnSunday] [bit] NOT NULL,
	[WorkOnMonday] [bit] NOT NULL,
	[WorkOnTuesday] [bit] NOT NULL,
	[WorkOnWednesday] [bit] NOT NULL,
	[WorkOnThursday] [bit] NOT NULL,
	[WorkOnFriday] [bit] NOT NULL,
	[WorkOnSaturday] [bit] NOT NULL,
	[AllowPermissions] [bit] NOT NULL,
	[MaxPermissionRequestsPerDay] [int] NULL,
	[MaxPermissionRequestsPerWeek] [int] NULL,
	[MaxPermissionRequestsPerMonth] [int] NULL,
	[MaxPermissionMinutesPerRequest] [int] NULL,
	[MaxPermissionMinutesPerDay] [int] NULL,
	[MaxPermissionMinutesPerWeek] [int] NULL,
	[MaxPermissionMinutesPerMonth] [int] NULL,
	[RequirePermissionApproval] [bit] NOT NULL,
	[RejectPermissionIfExceeded] [bit] NOT NULL,
	[DeductPermissionIfExceeded] [bit] NOT NULL,
	[AllowLateArrivalPermission] [bit] NOT NULL,
	[AllowEarlyLeavePermission] [bit] NOT NULL,
	[AllowDuringWorkPermission] [bit] NOT NULL,
	[LinkPermissionWithFingerprint] [bit] NOT NULL,
	[PermissionDeductionRatePerMinute] [decimal](19, 4) NULL,
 CONSTRAINT [PK_AttendancePolicies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[AttendancePolicies]') AND name = N'IX_AttendancePolicies_LeavePolicyTypeID')
CREATE NONCLUSTERED INDEX [IX_AttendancePolicies_LeavePolicyTypeID] ON [dbo].[AttendancePolicies]
(
	[LeavePolicyTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__18EBB532]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnSunday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__19DFD96B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnMonday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__1AD3FDA4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnTuesday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__1BC821DD]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnWednesday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__1CBC4616]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnThursday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__1DB06A4F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnFriday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__WorkO__1EA48E88]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  DEFAULT ((0)) FOR [WorkOnSaturday]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_AllowPermissions]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_AllowPermissions]  DEFAULT ((0)) FOR [AllowPermissions]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_RequirePermissionApproval]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_RequirePermissionApproval]  DEFAULT ((1)) FOR [RequirePermissionApproval]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_RejectPermissionIfExceeded]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_RejectPermissionIfExceeded]  DEFAULT ((1)) FOR [RejectPermissionIfExceeded]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_DeductPermissionIfExceeded]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_DeductPermissionIfExceeded]  DEFAULT ((0)) FOR [DeductPermissionIfExceeded]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_AllowLateArrivalPermission]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_AllowLateArrivalPermission]  DEFAULT ((1)) FOR [AllowLateArrivalPermission]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_AllowEarlyLeavePermission]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_AllowEarlyLeavePermission]  DEFAULT ((1)) FOR [AllowEarlyLeavePermission]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_AllowDuringWorkPermission]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_AllowDuringWorkPermission]  DEFAULT ((1)) FOR [AllowDuringWorkPermission]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_AttendancePolicies_LinkPermissionWithFingerprint]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendancePolicies] ADD  CONSTRAINT [DF_AttendancePolicies_LinkPermissionWithFingerprint]  DEFAULT ((1)) FOR [LinkPermissionWithFingerprint]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AttendanceRecords]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[AttendanceRecords](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[WorkDate] [date] NOT NULL,
	[CheckIn] [datetime2](7) NULL,
	[CheckOut] [datetime2](7) NULL,
	[Source] [nvarchar](20) NOT NULL,
	[LateMinutes] [int] NOT NULL,
	[EarlyLeaveMinutes] [int] NOT NULL,
	[OvertimeMinutes] [int] NOT NULL,
	[WorkedHours] [decimal](9, 2) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[Notes] [nvarchar](300) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [UX_Attendance_Emp_Date] UNIQUE NONCLUSTERED 
(
	[CompanyID] ASC,
	[EmployeeID] ASC,
	[WorkDate] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__Sourc__6F4A8121]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ('Web') FOR [Source]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__LateM__703EA55A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ((0)) FOR [LateMinutes]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__Early__7132C993]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ((0)) FOR [EarlyLeaveMinutes]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__Overt__7226EDCC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ((0)) FOR [OvertimeMinutes]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__Worke__731B1205]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ((0)) FOR [WorkedHours]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Attendanc__Statu__740F363E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[AttendanceRecords] ADD  DEFAULT ('Present') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[BankAccounts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[BankAccounts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[BankName] [nvarchar](200) NOT NULL,
	[BankNameEn] [nvarchar](200) NULL,
	[AccountNumber] [nvarchar](60) NULL,
	[IBAN] [nvarchar](60) NULL,
	[CurrencyId] [int] NULL,
	[GlAccountId] [int] NOT NULL,
	[OpeningBalance] [decimal](19, 4) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__BankAccou__Openi__1D7B6025]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[BankAccounts] ADD  DEFAULT ((0)) FOR [OpeningBalance]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__BankAccou__IsAct__1E6F845E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[BankAccounts] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[BankReconciliationLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[BankReconciliationLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[BankReconciliationId] [int] NOT NULL,
	[JournalEntryLineId] [int] NOT NULL,
	[Cleared] [bit] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__BankRecon__Clear__2704CA5F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[BankReconciliationLines] ADD  DEFAULT ((1)) FOR [Cleared]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[BankReconciliations]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[BankReconciliations](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[BankAccountId] [int] NOT NULL,
	[StatementDate] [date] NOT NULL,
	[StatementBalance] [decimal](19, 4) NOT NULL,
	[BookBalance] [decimal](19, 4) NOT NULL,
	[ClearedBalance] [decimal](19, 4) NOT NULL,
	[Difference] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__BankRecon__Statu__24285DB4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[BankReconciliations] ADD  DEFAULT ('Pending') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[BinLocations]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[BinLocations](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[Code] [nvarchar](60) NOT NULL,
	[Name] [nvarchar](200) NULL,
	[ParentId] [int] NULL,
	[IsActive] [bit] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__BinLocati__IsAct__68D28DBC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[BinLocations] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Branches]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Branches](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[Name] [nvarchar](max) NOT NULL,
	[NameAr] [nvarchar](max) NOT NULL,
	[Location] [nvarchar](max) NOT NULL,
	[CountryID] [int] NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PhoneNumber] [nvarchar](max) NOT NULL,
	[Email] [nvarchar](max) NOT NULL,
	[ImageUrl] [nvarchar](max) NULL,
	[Description] [nvarchar](max) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[FunctionalCurrencyId] [int] NULL,
	[CurrencyLockedAt] [datetime2](7) NULL,
 CONSTRAINT [PK_Branches] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Branches]') AND name = N'IX_Branches_CompanyID')
CREATE NONCLUSTERED INDEX [IX_Branches_CompanyID] ON [dbo].[Branches]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Branches]') AND name = N'IX_Branches_CountryID')
CREATE NONCLUSTERED INDEX [IX_Branches_CountryID] ON [dbo].[Branches]
(
	[CountryID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CampaignMembers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CampaignMembers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[CampaignId] [int] NOT NULL,
	[EntityType] [nvarchar](20) NOT NULL,
	[EntityId] [int] NOT NULL,
	[MemberName] [nvarchar](200) NULL,
	[Status] [nvarchar](20) NOT NULL,
	[RespondedAt] [datetime2](7) NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CampaignMembers]') AND name = N'IX_CampaignMembers_Campaign')
CREATE NONCLUSTERED INDEX [IX_CampaignMembers_Campaign] ON [dbo].[CampaignMembers]
(
	[CompanyID] ASC,
	[CampaignId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CampaignMembers]') AND name = N'UX_CampaignMembers')
CREATE UNIQUE NONCLUSTERED INDEX [UX_CampaignMembers] ON [dbo].[CampaignMembers]
(
	[CampaignId] ASC,
	[EntityType] ASC,
	[EntityId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CampMem_Status]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CampaignMembers] ADD  CONSTRAINT [DF_CampMem_Status]  DEFAULT ('Targeted') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Campaigns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Campaigns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NULL,
	[Channel] [nvarchar](60) NULL,
	[Status] [nvarchar](40) NOT NULL,
	[StartDate] [datetime2](7) NULL,
	[EndDate] [datetime2](7) NULL,
	[Budget] [decimal](19, 4) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[OwnerEmployeeId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Campaigns]') AND name = N'IX_Campaigns_Company')
CREATE NONCLUSTERED INDEX [IX_Campaigns_Company] ON [dbo].[Campaigns]
(
	[CompanyID] ASC,
	[Status] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Campaigns__Statu__5EDF0F2E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Campaigns] ADD  DEFAULT ('Planned') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Campaigns__Budge__5FD33367]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Campaigns] ADD  DEFAULT ((0)) FOR [Budget]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CashBoxes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CashBoxes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NULL,
	[CustodianEmployeeId] [int] NULL,
	[GlAccountId] [int] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__CashBoxes__IsAct__214BF109]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CashBoxes] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Companies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Companies](
	[CompanyID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyName] [nvarchar](max) NULL,
	[ComoanyNameAr] [nvarchar](max) NULL,
	[Address] [nvarchar](max) NOT NULL,
	[PhoneNumber] [nvarchar](max) NOT NULL,
	[Email] [nvarchar](max) NOT NULL,
	[PostalCode] [nvarchar](max) NULL,
	[Website] [nvarchar](max) NULL,
	[CountryID] [int] NOT NULL,
	[CompanyImage] [nvarchar](max) NULL,
	[CompanyTypeId] [int] NOT NULL,
	[RegistrationNumber] [nvarchar](max) NULL,
	[TaxNumber] [nvarchar](max) NULL,
	[Description] [nvarchar](max) NULL,
	[ParentCompany] [int] NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[DefaultCurrencyId] [int] NULL,
 CONSTRAINT [PK_Companies] PRIMARY KEY CLUSTERED 
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Companies]') AND name = N'IX_Companies_CompanyTypeId')
CREATE NONCLUSTERED INDEX [IX_Companies_CompanyTypeId] ON [dbo].[Companies]
(
	[CompanyTypeId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Companies]') AND name = N'IX_Companies_CountryID')
CREATE NONCLUSTERED INDEX [IX_Companies_CountryID] ON [dbo].[Companies]
(
	[CountryID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Companies]') AND name = N'IX_Companies_ParentCompany')
CREATE NONCLUSTERED INDEX [IX_Companies_ParentCompany] ON [dbo].[Companies]
(
	[ParentCompany] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CompanyTypes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CompanyTypes](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[NameAr] [nvarchar](max) NOT NULL,
	[NameEn] [nvarchar](max) NULL,
 CONSTRAINT [PK_CompanyTypes] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CostCenters]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CostCenters](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NOT NULL,
	[ParentId] [int] NULL,
	[SourceHierarchicalId] [int] NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__CostCente__IsAct__6BE40491]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CostCenters] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CountriesLookup]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CountriesLookup](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CountryName] [nvarchar](max) NOT NULL,
	[CountryNameAr] [nvarchar](max) NOT NULL,
	[NationalityName] [nvarchar](max) NOT NULL,
	[NationalityNameEn] [nvarchar](max) NOT NULL,
	[Flage] [nvarchar](max) NOT NULL,
 CONSTRAINT [PK_CountriesLookup] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmAccounts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmAccounts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NULL,
	[Industry] [nvarchar](100) NULL,
	[Phone] [nvarchar](50) NULL,
	[Email] [nvarchar](150) NULL,
	[Website] [nvarchar](150) NULL,
	[Address] [nvarchar](400) NULL,
	[Source] [nvarchar](80) NULL,
	[Segment] [nvarchar](80) NULL,
	[OwnerEmployeeId] [int] NULL,
	[CustomerId] [int] NULL,
	[IsActive] [bit] NOT NULL,
	[Notes] [nvarchar](max) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmAccounts]') AND name = N'IX_CrmAccounts_Cust')
CREATE NONCLUSTERED INDEX [IX_CrmAccounts_Cust] ON [dbo].[CrmAccounts]
(
	[CompanyID] ASC,
	[CustomerId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmAccounts_Active]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmAccounts] ADD  CONSTRAINT [DF_CrmAccounts_Active]  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmContacts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmContacts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[AccountId] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[Title] [nvarchar](120) NULL,
	[Phone] [nvarchar](50) NULL,
	[Email] [nvarchar](150) NULL,
	[IsPrimary] [bit] NOT NULL,
	[OwnerEmployeeId] [int] NULL,
	[Notes] [nvarchar](max) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmContacts]') AND name = N'IX_CrmContacts_Acc')
CREATE NONCLUSTERED INDEX [IX_CrmContacts_Acc] ON [dbo].[CrmContacts]
(
	[CompanyID] ASC,
	[AccountId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmContacts_Primary]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmContacts] ADD  CONSTRAINT [DF_CrmContacts_Primary]  DEFAULT ((0)) FOR [IsPrimary]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmListMembers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmListMembers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ListId] [int] NOT NULL,
	[EntityType] [nvarchar](20) NOT NULL,
	[EntityId] [int] NOT NULL,
	[MemberName] [nvarchar](200) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmListMembers]') AND name = N'IX_CrmListMembers_List')
CREATE NONCLUSTERED INDEX [IX_CrmListMembers_List] ON [dbo].[CrmListMembers]
(
	[CompanyID] ASC,
	[ListId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmListMembers]') AND name = N'UX_CrmListMembers')
CREATE UNIQUE NONCLUSTERED INDEX [UX_CrmListMembers] ON [dbo].[CrmListMembers]
(
	[ListId] ASC,
	[EntityType] ASC,
	[EntityId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmMarketingLists]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmMarketingLists](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NULL,
	[Description] [nvarchar](1000) NULL,
	[IsActive] [bit] NOT NULL,
	[OwnerEmployeeId] [int] NULL,
	[CreatedBy] [nvarchar](256) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmMarketingLists]') AND name = N'IX_CrmMarketingLists_Company')
CREATE NONCLUSTERED INDEX [IX_CrmMarketingLists_Company] ON [dbo].[CrmMarketingLists]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_MktList_Active]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmMarketingLists] ADD  CONSTRAINT [DF_MktList_Active]  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmPipelines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmPipelines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](120) NOT NULL,
	[NameEn] [nvarchar](120) NULL,
	[IsDefault] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmPipelines_Def]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelines] ADD  CONSTRAINT [DF_CrmPipelines_Def]  DEFAULT ((0)) FOR [IsDefault]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmPipelines_Act]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelines] ADD  CONSTRAINT [DF_CrmPipelines_Act]  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmPipelineStages]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmPipelineStages](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PipelineId] [int] NOT NULL,
	[Name] [nvarchar](80) NOT NULL,
	[NameEn] [nvarchar](80) NULL,
	[Sort] [int] NOT NULL,
	[Probability] [int] NOT NULL,
	[IsWon] [bit] NOT NULL,
	[IsLost] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmPipelineStages]') AND name = N'IX_CrmPipelineStages_Pl')
CREATE NONCLUSTERED INDEX [IX_CrmPipelineStages_Pl] ON [dbo].[CrmPipelineStages]
(
	[CompanyID] ASC,
	[PipelineId] ASC,
	[Sort] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__CrmPipelin__Sort__74CE504D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelineStages] ADD  DEFAULT ((0)) FOR [Sort]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__CrmPipeli__Proba__75C27486]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelineStages] ADD  DEFAULT ((0)) FOR [Probability]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmStage_Won]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelineStages] ADD  CONSTRAINT [DF_CrmStage_Won]  DEFAULT ((0)) FOR [IsWon]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_CrmStage_Lost]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[CrmPipelineStages] ADD  CONSTRAINT [DF_CrmStage_Lost]  DEFAULT ((0)) FOR [IsLost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[CrmUserRoles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[CrmUserRoles](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeId] [int] NOT NULL,
	[Role] [nvarchar](40) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[CrmUserRoles]') AND name = N'IX_CrmUserRoles_Emp')
CREATE NONCLUSTERED INDEX [IX_CrmUserRoles_Emp] ON [dbo].[CrmUserRoles]
(
	[CompanyID] ASC,
	[EmployeeId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Currencies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Currencies](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[Code] [nvarchar](3) NOT NULL,
	[Symbol] [nvarchar](10) NULL,
	[Name] [nvarchar](100) NOT NULL,
	[NameEn] [nvarchar](100) NOT NULL,
	[DecimalPlaces] [tinyint] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Currencie__Decim__4D5F7D71]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Currencies] ADD  DEFAULT ((2)) FOR [DecimalPlaces]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Customers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Customers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[TaxRegNo] [nvarchar](50) NULL,
	[Address] [nvarchar](500) NULL,
	[ControlAccountId] [int] NOT NULL,
	[CurrencyId] [int] NULL,
	[PaymentTermsDays] [int] NULL,
	[CreditLimit] [decimal](19, 4) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
	[Phone] [nvarchar](50) NULL,
	[Email] [nvarchar](150) NULL,
	[ContactPerson] [nvarchar](150) NULL,
	[Segment] [nvarchar](80) NULL,
	[ShippingAddress] [nvarchar](500) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Customers__IsAct__7755B73D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Customers] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DeliveryNoteLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[DeliveryNoteLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[DeliveryNoteId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[BatchNo] [nvarchar](80) NULL,
	[SerialNo] [nvarchar](80) NULL,
	[SalesOrderLineId] [int] NULL,
	[StockMovementId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[DeliveryNoteLines]') AND name = N'IX_DNLines_DN')
CREATE NONCLUSTERED INDEX [IX_DNLines_DN] ON [dbo].[DeliveryNoteLines]
(
	[DeliveryNoteId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__DeliveryNot__Qty__2B947552]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DeliveryNoteLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__DeliveryN__UnitC__2C88998B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DeliveryNoteLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__DeliveryN__LineT__2D7CBDC4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DeliveryNoteLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DeliveryNotes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[DeliveryNotes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[DeliveryNo] [nvarchar](40) NULL,
	[DeliveryDate] [datetime] NOT NULL,
	[CustomerId] [int] NULL,
	[WarehouseId] [int] NOT NULL,
	[SalesOrderId] [int] NULL,
	[Status] [nvarchar](20) NOT NULL,
	[TotalCost] [decimal](19, 4) NOT NULL,
	[InvoiceId] [int] NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
	[CurrencyId] [int] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[DeliveryNotes]') AND name = N'IX_DeliveryNotes_SO')
CREATE NONCLUSTERED INDEX [IX_DeliveryNotes_SO] ON [dbo].[DeliveryNotes]
(
	[CompanyID] ASC,
	[SalesOrderId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__DeliveryN__Statu__27C3E46E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DeliveryNotes] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__DeliveryN__Total__28B808A7]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DeliveryNotes] ADD  DEFAULT ((0)) FOR [TotalCost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DepreciationLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[DepreciationLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[DepreciationRunId] [int] NOT NULL,
	[FixedAssetId] [int] NOT NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[AccumulatedAfter] [decimal](19, 4) NOT NULL,
	[NetBookValueAfter] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__Amoun__3B0BC30C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationLines] ADD  DEFAULT ((0)) FOR [Amount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__Accum__3BFFE745]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationLines] ADD  DEFAULT ((0)) FOR [AccumulatedAfter]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__NetBo__3CF40B7E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationLines] ADD  DEFAULT ((0)) FOR [NetBookValueAfter]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DepreciationRuns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[DepreciationRuns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PeriodDate] [date] NOT NULL,
	[RunDate] [date] NOT NULL,
	[TotalAmount] [decimal](19, 4) NOT NULL,
	[AssetCount] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__Total__36470DEF]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationRuns] ADD  DEFAULT ((0)) FOR [TotalAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__Asset__373B3228]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationRuns] ADD  DEFAULT ((0)) FOR [AssetCount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Depreciat__Statu__382F5661]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[DepreciationRuns] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Employee](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[FirstName] [nvarchar](max) NOT NULL,
	[LastName] [nvarchar](max) NOT NULL,
	[FullName] [nvarchar](max) NOT NULL,
	[Address] [nvarchar](max) NOT NULL,
	[PhoneNumber] [nvarchar](max) NOT NULL,
	[Email] [nvarchar](max) NOT NULL,
	[CountryID] [int] NULL,
	[JobTitleID] [int] NOT NULL,
	[BranchID] [int] NULL,
	[EmpCompanyID] [int] NOT NULL,
	[ProfileImage] [nvarchar](max) NOT NULL,
	[DateOfBirth] [datetime2](7) NOT NULL,
	[Gender] [nvarchar](max) NOT NULL,
	[MaritalStatus] [nvarchar](max) NOT NULL,
	[DateOfJoining] [datetime2](7) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[UserId] [nvarchar](450) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[SalaryPoliciesID] [int] NULL,
	[DepartmentID] [int] NULL,
	[AdministrativeStructureID] [int] NULL,
	[FullNameEn] [nvarchar](200) NULL,
	[EmploymentType] [nvarchar](50) NULL,
 CONSTRAINT [PK_Employee] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_BranchID')
CREATE NONCLUSTERED INDEX [IX_Employee_BranchID] ON [dbo].[Employee]
(
	[BranchID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_CountryID')
CREATE NONCLUSTERED INDEX [IX_Employee_CountryID] ON [dbo].[Employee]
(
	[CountryID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_EmpCompanyID')
CREATE NONCLUSTERED INDEX [IX_Employee_EmpCompanyID] ON [dbo].[Employee]
(
	[EmpCompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_JobTitleID')
CREATE NONCLUSTERED INDEX [IX_Employee_JobTitleID] ON [dbo].[Employee]
(
	[JobTitleID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_SalaryPoliciesID')
CREATE NONCLUSTERED INDEX [IX_Employee_SalaryPoliciesID] ON [dbo].[Employee]
(
	[SalaryPoliciesID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Employee]') AND name = N'IX_Employee_UserId')
CREATE UNIQUE NONCLUSTERED INDEX [IX_Employee_UserId] ON [dbo].[Employee]
(
	[UserId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EmployeeDocuments]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[EmployeeDocuments](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[DocType] [nvarchar](40) NOT NULL,
	[DocNumber] [nvarchar](100) NULL,
	[FilePath] [nvarchar](400) NULL,
	[IssueDate] [datetime2](7) NULL,
	[ExpiryDate] [datetime2](7) NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[EmployeeDocuments]') AND name = N'IX_EmployeeDocuments_Emp')
CREATE NONCLUSTERED INDEX [IX_EmployeeDocuments_Emp] ON [dbo].[EmployeeDocuments]
(
	[CompanyID] ASC,
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[EmployeeDocuments]') AND name = N'IX_EmployeeDocuments_Expiry')
CREATE NONCLUSTERED INDEX [IX_EmployeeDocuments_Expiry] ON [dbo].[EmployeeDocuments]
(
	[CompanyID] ASC,
	[ExpiryDate] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EmploymentContracts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[EmploymentContracts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[ContractType] [nvarchar](40) NOT NULL,
	[StartDate] [datetime2](7) NOT NULL,
	[EndDate] [datetime2](7) NULL,
	[Status] [nvarchar](40) NOT NULL,
	[FilePath] [nvarchar](400) NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[EmploymentContracts]') AND name = N'IX_EmploymentContracts_Emp')
CREATE NONCLUSTERED INDEX [IX_EmploymentContracts_Emp] ON [dbo].[EmploymentContracts]
(
	[CompanyID] ASC,
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EtaSettings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[EtaSettings](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Enabled] [bit] NOT NULL,
	[Environment] [nvarchar](20) NOT NULL,
	[ClientId] [nvarchar](120) NULL,
	[TaxpayerRin] [nvarchar](50) NULL,
	[ActivityCode] [nvarchar](20) NULL,
	[UpdatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__EtaSettin__Enabl__4B422AD5]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[EtaSettings] ADD  DEFAULT ((0)) FOR [Enabled]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__EtaSettin__Envir__4C364F0E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[EtaSettings] ADD  DEFAULT ('Preprod') FOR [Environment]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ExchangeRates]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ExchangeRates](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CurrencyId] [int] NOT NULL,
	[RateDate] [date] NOT NULL,
	[Rate] [decimal](19, 8) NOT NULL,
	[RateType] [nvarchar](20) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__ExchangeR__RateT__503BEA1C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[ExchangeRates] ADD  DEFAULT ('Standard') FOR [RateType]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FinalSettlements]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[FinalSettlements](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[EmployeeName] [nvarchar](200) NULL,
	[TerminationDate] [datetime2](7) NOT NULL,
	[Reason] [nvarchar](500) NULL,
	[ServiceYears] [decimal](19, 4) NOT NULL,
	[LeaveDays] [int] NOT NULL,
	[LeaveValue] [decimal](19, 4) NOT NULL,
	[Gratuity] [decimal](19, 4) NOT NULL,
	[OtherEarnings] [decimal](19, 4) NOT NULL,
	[Deductions] [decimal](19, 4) NOT NULL,
	[NetSettlement] [decimal](19, 4) NOT NULL,
	[JournalEntryId] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[FinalSettlements]') AND name = N'IX_FinalSettlements_Emp')
CREATE NONCLUSTERED INDEX [IX_FinalSettlements_Emp] ON [dbo].[FinalSettlements]
(
	[CompanyID] ASC,
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FiscalPeriods]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[FiscalPeriods](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[FiscalYearId] [int] NOT NULL,
	[PeriodNo] [tinyint] NOT NULL,
	[StartDate] [date] NOT NULL,
	[EndDate] [date] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FiscalPer__Statu__5CA1C101]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FiscalPeriods] ADD  DEFAULT ('Open') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FiscalYears]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[FiscalYears](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](50) NOT NULL,
	[StartDate] [date] NOT NULL,
	[EndDate] [date] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FiscalYea__Statu__59C55456]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FiscalYears] ADD  DEFAULT ('Open') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FixedAssets]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[FixedAssets](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[AssetNo] [nvarchar](40) NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[CategoryId] [int] NULL,
	[AcquisitionDate] [date] NOT NULL,
	[Cost] [decimal](19, 4) NOT NULL,
	[SalvageValue] [decimal](19, 4) NOT NULL,
	[UsefulLifeMonths] [int] NOT NULL,
	[DepreciationMethod] [nvarchar](30) NOT NULL,
	[CostAccountId] [int] NOT NULL,
	[AccumDepAccountId] [int] NOT NULL,
	[DepExpenseAccountId] [int] NOT NULL,
	[CostCenterId] [int] NULL,
	[AccumulatedDepreciation] [decimal](19, 4) NOT NULL,
	[LastDepreciationDate] [date] NULL,
	[Status] [nvarchar](20) NOT NULL,
	[DisposalDate] [date] NULL,
	[DisposalProceeds] [decimal](19, 4) NULL,
	[AcquisitionJournalEntryId] [int] NULL,
	[DisposalJournalEntryId] [int] NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsset__Cost__2EA5EC27]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ((0)) FOR [Cost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsse__Salva__2F9A1060]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ((0)) FOR [SalvageValue]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsse__Usefu__308E3499]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ((60)) FOR [UsefulLifeMonths]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsse__Depre__318258D2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ('StraightLine') FOR [DepreciationMethod]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsse__Accum__32767D0B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ((0)) FOR [AccumulatedDepreciation]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__FixedAsse__Statu__336AA144]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[FixedAssets] ADD  DEFAULT ('Active') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FxRevaluationRuns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[FxRevaluationRuns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[AsOfDate] [date] NOT NULL,
	[RateType] [nvarchar](20) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[TotalArDiff] [decimal](19, 4) NOT NULL,
	[TotalApDiff] [decimal](19, 4) NOT NULL,
	[JournalEntryId] [int] NULL,
	[ReversalEntryId] [int] NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[GoodsReceiptLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[GoodsReceiptLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[GoodsReceiptId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[BatchNo] [nvarchar](80) NULL,
	[ExpiryDate] [date] NULL,
	[SerialNo] [nvarchar](80) NULL,
	[PurchaseOrderLineId] [int] NULL,
	[StockMovementId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[GoodsReceiptLines]') AND name = N'IX_GRLines_GR')
CREATE NONCLUSTERED INDEX [IX_GRLines_GR] ON [dbo].[GoodsReceiptLines]
(
	[GoodsReceiptId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__GoodsReceip__Qty__15A53433]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[GoodsReceiptLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__GoodsRece__UnitC__1699586C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[GoodsReceiptLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__GoodsRece__LineT__178D7CA5]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[GoodsReceiptLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[GoodsReceipts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[GoodsReceipts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ReceiptNo] [nvarchar](40) NULL,
	[ReceiptDate] [datetime] NOT NULL,
	[VendorId] [int] NULL,
	[WarehouseId] [int] NOT NULL,
	[PurchaseOrderId] [int] NULL,
	[Status] [nvarchar](20) NOT NULL,
	[TotalCost] [decimal](19, 4) NOT NULL,
	[InvoiceId] [int] NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
	[CurrencyId] [int] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[GoodsReceipts]') AND name = N'IX_GoodsReceipts_PO')
CREATE NONCLUSTERED INDEX [IX_GoodsReceipts_PO] ON [dbo].[GoodsReceipts]
(
	[CompanyID] ASC,
	[PurchaseOrderId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__GoodsRece__Statu__11D4A34F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[GoodsReceipts] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__GoodsRece__Total__12C8C788]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[GoodsReceipts] ADD  DEFAULT ((0)) FOR [TotalCost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Hierarchicals]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Hierarchicals](
	[H_ID] [int] IDENTITY(1,1) NOT NULL,
	[H_Name] [nvarchar](max) NULL,
	[H_NameEn] [nvarchar](max) NULL,
	[H_Parent] [int] NULL,
	[H_Notes] [nvarchar](max) NULL,
	[H_Type] [int] NULL,
	[H_ObjectID] [int] NULL,
	[Sort] [int] NULL,
	[IsActive] [bit] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CreatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[deputyID] [int] NULL,
	[updatedBy] [int] NULL,
 CONSTRAINT [PK_Hierarchicals] PRIMARY KEY CLUSTERED 
(
	[H_ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Hierarchicals]') AND name = N'IX_Hierarchicals_H_Parent')
CREATE NONCLUSTERED INDEX [IX_Hierarchicals_H_Parent] ON [dbo].[Hierarchicals]
(
	[H_Parent] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Hierarchicals]') AND name = N'IX_Hierarchicals_H_Type')
CREATE NONCLUSTERED INDEX [IX_Hierarchicals_H_Type] ON [dbo].[Hierarchicals]
(
	[H_Type] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[HierarchicalTypes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[HierarchicalTypes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[TypeNameAr] [nvarchar](max) NULL,
	[TypeNameEn] [nvarchar](max) NULL,
 CONSTRAINT [PK_HierarchicalTypes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[IntegrityCheckRuns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[IntegrityCheckRuns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[RunAt] [datetime2](7) NOT NULL,
	[Source] [nvarchar](20) NOT NULL,
	[AllOk] [bit] NOT NULL,
	[FailedCount] [int] NOT NULL,
	[Summary] [nvarchar](max) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Integrity__Sourc__66B53B20]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[IntegrityCheckRuns] ADD  DEFAULT ('Manual') FOR [Source]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Integrity__AllOk__67A95F59]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[IntegrityCheckRuns] ADD  DEFAULT ((1)) FOR [AllOk]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Integrity__Faile__689D8392]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[IntegrityCheckRuns] ADD  DEFAULT ((0)) FOR [FailedCount]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[InventoryApprovals]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[InventoryApprovals](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[DocType] [nvarchar](40) NOT NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[PayloadJson] [nvarchar](max) NULL,
	[Status] [nvarchar](20) NOT NULL,
	[RequestedByEmployeeId] [int] NULL,
	[RequestedAt] [datetime] NULL,
	[DecidedByEmployeeId] [int] NULL,
	[DecidedAt] [datetime] NULL,
	[DecisionNote] [nvarchar](400) NULL,
	[ResultDocNo] [nvarchar](60) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[InventoryApprovals]') AND name = N'IX_InvApprovals_Status')
CREATE NONCLUSTERED INDEX [IX_InvApprovals_Status] ON [dbo].[InventoryApprovals]
(
	[CompanyID] ASC,
	[Status] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Inventory__Amoun__50C5FA01]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventoryApprovals] ADD  DEFAULT ((0)) FOR [Amount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Inventory__Statu__51BA1E3A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventoryApprovals] ADD  DEFAULT ('Pending') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[InventorySettings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[InventorySettings](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[InterBranchTransferMode] [nvarchar](20) NOT NULL,
	[ApprovalThreshold] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime] NULL,
	[WriteOffMode] [nvarchar](40) NOT NULL,
	[ConvertBasePriceForForeignDocs] [bit] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[InventorySettings]') AND name = N'UX_InventorySettings_Co')
CREATE UNIQUE NONCLUSTERED INDEX [UX_InventorySettings_Co] ON [dbo].[InventorySettings]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Inventory__Inter__4B0D20AB]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventorySettings] ADD  DEFAULT ('CostCenterPosting') FOR [InterBranchTransferMode]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Inventory__Appro__4C0144E4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventorySettings] ADD  DEFAULT ((0)) FOR [ApprovalThreshold]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Inventory__Write__52AE4273]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventorySettings] ADD  DEFAULT ('SeparateDocument') FOR [WriteOffMode]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_InvSettings_ConvBase]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[InventorySettings] ADD  CONSTRAINT [DF_InvSettings_ConvBase]  DEFAULT ((1)) FOR [ConvertBasePriceForForeignDocs]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[InventoryUserRoles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[InventoryUserRoles](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeId] [int] NOT NULL,
	[Role] [nvarchar](40) NOT NULL,
	[ScopeBranchId] [int] NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[InventoryUserRoles]') AND name = N'IX_InvUserRoles_Emp')
CREATE NONCLUSTERED INDEX [IX_InvUserRoles_Emp] ON [dbo].[InventoryUserRoles]
(
	[CompanyID] ASC,
	[EmployeeId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ItemBarcodes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ItemBarcodes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[ItemId] [int] NOT NULL,
	[Barcode] [nvarchar](60) NOT NULL,
	[UoMId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[ItemBarcodes]') AND name = N'UX_ItemBarcodes_Barcode')
CREATE UNIQUE NONCLUSTERED INDEX [UX_ItemBarcodes_Barcode] ON [dbo].[ItemBarcodes]
(
	[Barcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ItemCategories]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ItemCategories](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NOT NULL,
	[ParentId] [int] NULL,
	[InventoryAccountId] [int] NULL,
	[CogsAccountId] [int] NULL,
	[AdjustmentAccountId] [int] NULL,
	[GrniAccountId] [int] NULL,
	[DefaultCostingMethod] [nvarchar](20) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ModifiedBy] [nvarchar](450) NULL,
	[ModifiedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__ItemCateg__IsAct__6D9742D9]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[ItemCategories] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ItemComponents]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ItemComponents](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ParentItemId] [int] NOT NULL,
	[ComponentItemId] [int] NOT NULL,
	[Quantity] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[SortOrder] [int] NOT NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[ItemComponents]') AND name = N'IX_ItemComponents_Parent')
CREATE NONCLUSTERED INDEX [IX_ItemComponents_Parent] ON [dbo].[ItemComponents]
(
	[ParentItemId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_ItemComponents_Qty]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[ItemComponents] ADD  CONSTRAINT [DF_ItemComponents_Qty]  DEFAULT ((1)) FOR [Quantity]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_ItemComponents_Sort]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[ItemComponents] ADD  CONSTRAINT [DF_ItemComponents_Sort]  DEFAULT ((0)) FOR [SortOrder]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Items]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Items](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ItemCode] [nvarchar](60) NOT NULL,
	[Barcode] [nvarchar](60) NOT NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[ItemCategoryId] [int] NOT NULL,
	[ItemType] [nvarchar](20) NOT NULL,
	[BaseUoMId] [int] NOT NULL,
	[PurchaseUoMId] [int] NULL,
	[SalesUoMId] [int] NULL,
	[CostingMethod] [nvarchar](20) NULL,
	[TrackBatch] [bit] NOT NULL,
	[TrackExpiry] [bit] NOT NULL,
	[TrackSerial] [bit] NOT NULL,
	[DefaultTaxCodeId] [int] NULL,
	[ItemEgsCode] [nvarchar](60) NULL,
	[SalesPrice] [decimal](19, 4) NULL,
	[OpeningCost] [decimal](19, 4) NULL,
	[ImagePath] [nvarchar](300) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ModifiedBy] [nvarchar](450) NULL,
	[ModifiedAt] [datetime2](7) NULL,
	[IsComposite] [bit] NOT NULL,
	[CompositeType] [nvarchar](20) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Items]') AND name = N'UX_Items_Company_Barcode')
CREATE UNIQUE NONCLUSTERED INDEX [UX_Items_Company_Barcode] ON [dbo].[Items]
(
	[CompanyID] ASC,
	[Barcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Items]') AND name = N'UX_Items_Company_Code')
CREATE UNIQUE NONCLUSTERED INDEX [UX_Items_Company_Code] ON [dbo].[Items]
(
	[CompanyID] ASC,
	[ItemCode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Items__ItemType__59904A2C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  DEFAULT ('Stockable') FOR [ItemType]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Items__TrackBatc__5A846E65]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  DEFAULT ((0)) FOR [TrackBatch]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Items__TrackExpi__5B78929E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  DEFAULT ((0)) FOR [TrackExpiry]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Items__TrackSeri__5C6CB6D7]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  DEFAULT ((0)) FOR [TrackSerial]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Items__IsActive__5D60DB10]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_Items_IsComposite]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Items] ADD  CONSTRAINT [DF_Items_IsComposite]  DEFAULT ((0)) FOR [IsComposite]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ItemWarehouseSettings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ItemWarehouseSettings](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[ItemId] [int] NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[ReorderPoint] [decimal](19, 4) NULL,
	[MinQty] [decimal](19, 4) NULL,
	[MaxQty] [decimal](19, 4) NULL,
	[SafetyStock] [decimal](19, 4) NULL,
	[LeadTimeDays] [int] NULL,
	[DefaultBinLocationId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[JobTitles]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[JobTitles](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[Title] [nvarchar](max) NOT NULL,
	[TitleAr] [nvarchar](max) NOT NULL,
	[Description] [nvarchar](max) NOT NULL,
 CONSTRAINT [PK_JobTitles] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[JournalEntries]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[JournalEntries](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EntryNo] [nvarchar](40) NULL,
	[EntryDate] [date] NOT NULL,
	[FiscalPeriodId] [int] NOT NULL,
	[JournalType] [nvarchar](20) NOT NULL,
	[SourceType] [nvarchar](40) NULL,
	[SourceId] [int] NULL,
	[CurrencyId] [int] NOT NULL,
	[Description] [nvarchar](500) NULL,
	[DescriptionEn] [nvarchar](500) NULL,
	[Status] [nvarchar](20) NOT NULL,
	[ReversedByEntryId] [int] NULL,
	[PostedBy] [int] NULL,
	[PostedAt] [datetime2](7) NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[JournalEntries]') AND name = N'UX_JE_Company_EntryNo')
CREATE UNIQUE NONCLUSTERED INDEX [UX_JE_Company_EntryNo] ON [dbo].[JournalEntries]
(
	[CompanyID] ASC,
	[EntryNo] ASC
)
WHERE ([EntryNo] IS NOT NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__JournalEn__Statu__5F7E2DAC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[JournalEntries] ADD  DEFAULT ('Draft') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[JournalEntryLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[JournalEntryId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[AccountId] [int] NOT NULL,
	[Debit] [decimal](19, 4) NOT NULL,
	[Credit] [decimal](19, 4) NOT NULL,
	[CostCenterId] [int] NULL,
	[ProjectId] [int] NULL,
	[EmployeeId] [int] NULL,
	[CurrencyId] [int] NULL,
	[ForeignAmount] [decimal](19, 4) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[Description] [nvarchar](500) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]') AND name = N'IX_JEL_Account')
CREATE NONCLUSTERED INDEX [IX_JEL_Account] ON [dbo].[JournalEntryLines]
(
	[AccountId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__JournalEn__Debit__662B2B3B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[JournalEntryLines] ADD  DEFAULT ((0)) FOR [Debit]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__JournalEn__Credi__671F4F74]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[JournalEntryLines] ADD  DEFAULT ((0)) FOR [Credit]
END

GO
IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'[dbo].[CK_JEL_OneSide]') AND parent_object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]'))
ALTER TABLE [dbo].[JournalEntryLines]  WITH CHECK ADD  CONSTRAINT [CK_JEL_OneSide] CHECK  (([Debit]=(0) OR [Credit]=(0)))
GO
IF  EXISTS (SELECT * FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'[dbo].[CK_JEL_OneSide]') AND parent_object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]'))
ALTER TABLE [dbo].[JournalEntryLines] CHECK CONSTRAINT [CK_JEL_OneSide]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LandedCostCharges]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LandedCostCharges](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LandedCostId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[Description] [nvarchar](200) NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[AccountId] [int] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LandedCostCharges]') AND name = N'IX_LandedCostCharges_L')
CREATE NONCLUSTERED INDEX [IX_LandedCostCharges_L] ON [dbo].[LandedCostCharges]
(
	[LandedCostId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LandedCos__Amoun__4830B400]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LandedCostCharges] ADD  DEFAULT ((0)) FOR [Amount]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LandedCosts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LandedCosts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[LandedNo] [nvarchar](40) NULL,
	[LandedDate] [datetime] NOT NULL,
	[GoodsReceiptId] [int] NOT NULL,
	[AllocationMethod] [nvarchar](20) NOT NULL,
	[TotalAmount] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LandedCosts]') AND name = N'IX_LandedCosts_GR')
CREATE NONCLUSTERED INDEX [IX_LandedCosts_GR] ON [dbo].[LandedCosts]
(
	[CompanyID] ASC,
	[GoodsReceiptId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LandedCos__Alloc__436BFEE3]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LandedCosts] ADD  DEFAULT ('Value') FOR [AllocationMethod]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LandedCos__Total__4460231C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LandedCosts] ADD  DEFAULT ((0)) FOR [TotalAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LandedCos__Statu__45544755]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LandedCosts] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Leads]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Leads](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[Company] [nvarchar](200) NULL,
	[Phone] [nvarchar](60) NULL,
	[Email] [nvarchar](160) NULL,
	[Source] [nvarchar](80) NULL,
	[Segment] [nvarchar](80) NULL,
	[EstimatedValue] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](40) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[CustomerId] [int] NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CampaignId] [int] NULL,
	[OwnerEmployeeId] [int] NULL,
	[AccountId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Leads]') AND name = N'IX_Leads_Company')
CREATE NONCLUSTERED INDEX [IX_Leads_Company] ON [dbo].[Leads]
(
	[CompanyID] ASC,
	[Status] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Leads__Estimated__52793849]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Leads] ADD  DEFAULT ((0)) FOR [EstimatedValue]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Leads__Status__536D5C82]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Leads] ADD  DEFAULT ('New') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveApprovalSteps]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveApprovalSteps](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LeaveRequestID] [int] NOT NULL,
	[Level] [int] NOT NULL,
	[ApproverEmployeeID] [int] NOT NULL,
	[Status] [int] NOT NULL,
	[DecisionAt] [datetime2](7) NULL,
	[DecisionNote] [nvarchar](max) NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LeaveAppr__Statu__489AC854]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LeaveApprovalSteps] ADD  DEFAULT ((0)) FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveCarryOvers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveCarryOvers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[LeaveTypeID] [int] NOT NULL,
	[Year] [int] NOT NULL,
	[Days] [int] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeaveCarryOvers]') AND name = N'IX_LeaveCarryOvers_Lookup')
CREATE NONCLUSTERED INDEX [IX_LeaveCarryOvers_Lookup] ON [dbo].[LeaveCarryOvers]
(
	[CompanyID] ASC,
	[EmployeeID] ASC,
	[LeaveTypeID] ASC,
	[Year] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveEncashments]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveEncashments](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[EmployeeName] [nvarchar](200) NULL,
	[LeaveTypeID] [int] NOT NULL,
	[Year] [int] NOT NULL,
	[Days] [int] NOT NULL,
	[DailyRate] [decimal](19, 4) NOT NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[EncashDate] [datetime2](7) NOT NULL,
	[JournalEntryId] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeaveEncashments]') AND name = N'IX_LeaveEncashments_Emp')
CREATE NONCLUSTERED INDEX [IX_LeaveEncashments_Emp] ON [dbo].[LeaveEncashments]
(
	[CompanyID] ASC,
	[EmployeeID] ASC,
	[LeaveTypeID] ASC,
	[Year] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeavePolicies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeavePolicies](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LeavePolicyTypeID] [int] NOT NULL,
	[EntitlementDaysPerYear] [int] NOT NULL,
	[CarryOverLimit] [int] NOT NULL,
	[NoticePeriodDays] [int] NOT NULL,
	[RequiresMedicalCertificate] [bit] NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[LeaveTypeID] [int] NOT NULL,
 CONSTRAINT [PK_LeavePolicies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeavePolicies]') AND name = N'IX_LeavePolicies_LeavePolicyTypeID')
CREATE NONCLUSTERED INDEX [IX_LeavePolicies_LeavePolicyTypeID] ON [dbo].[LeavePolicies]
(
	[LeavePolicyTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeavePolicies]') AND name = N'IX_LeavePolicies_LeaveTypeID')
CREATE NONCLUSTERED INDEX [IX_LeavePolicies_LeaveTypeID] ON [dbo].[LeavePolicies]
(
	[LeaveTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LeavePoli__Leave__7A672E12]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LeavePolicies] ADD  DEFAULT ((0)) FOR [LeaveTypeID]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveProvisionRuns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveProvisionRuns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[AsOfDate] [datetime2](7) NOT NULL,
	[TotalDays] [decimal](19, 4) NOT NULL,
	[TotalAmount] [decimal](19, 4) NOT NULL,
	[Adjustment] [decimal](19, 4) NOT NULL,
	[JournalEntryId] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveRequests]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveRequests](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[LeaveTypeID] [int] NOT NULL,
	[StartDate] [datetime2](7) NOT NULL,
	[EndDate] [datetime2](7) NOT NULL,
	[Days] [int] NOT NULL,
	[Reason] [nvarchar](max) NULL,
	[Status] [int] NOT NULL,
	[ApproverEmployeeID] [int] NULL,
	[DecisionAt] [datetime2](7) NULL,
	[DecisionNote] [nvarchar](max) NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[CurrentLevel] [int] NOT NULL,
	[CurrentApproverEmployeeID] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeaveRequests]') AND name = N'IX_LeaveRequests_EmployeeID')
CREATE NONCLUSTERED INDEX [IX_LeaveRequests_EmployeeID] ON [dbo].[LeaveRequests]
(
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[LeaveRequests]') AND name = N'IX_LeaveRequests_LeaveTypeID')
CREATE NONCLUSTERED INDEX [IX_LeaveRequests_LeaveTypeID] ON [dbo].[LeaveRequests]
(
	[LeaveTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__LeaveRequ__Statu__3F115E1A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LeaveRequests] ADD  DEFAULT ((0)) FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_LR_CurrentLevel]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LeaveRequests] ADD  CONSTRAINT [DF_LR_CurrentLevel]  DEFAULT ((0)) FOR [CurrentLevel]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[LeaveTypes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[LeaveTypes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[NameAr] [nvarchar](max) NOT NULL,
	[NameEn] [nvarchar](max) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[Notes] [nvarchar](max) NULL,
	[IsEncashable] [bit] NOT NULL,
 CONSTRAINT [PK_LeaveTypes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_LeaveTypes_IsEncashable]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[LeaveTypes] ADD  CONSTRAINT [DF_LeaveTypes_IsEncashable]  DEFAULT ((0)) FOR [IsEncashable]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Notifications](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[RecipientEmployeeID] [int] NOT NULL,
	[TitleAr] [nvarchar](max) NULL,
	[TitleEn] [nvarchar](max) NULL,
	[BodyAr] [nvarchar](max) NULL,
	[BodyEn] [nvarchar](max) NULL,
	[Type] [nvarchar](100) NULL,
	[RefId] [int] NULL,
	[IsRead] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Notifications]') AND name = N'IX_Notifications_Recipient')
CREATE NONCLUSTERED INDEX [IX_Notifications_Recipient] ON [dbo].[Notifications]
(
	[RecipientEmployeeID] ASC,
	[IsRead] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Notificat__IsRea__43D61337]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Notifications] ADD  DEFAULT ((0)) FOR [IsRead]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[NumberSequences]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[NumberSequences](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[SequenceKey] [nvarchar](40) NOT NULL,
	[FiscalYearId] [int] NULL,
	[Prefix] [nvarchar](20) NULL,
	[NextNumber] [int] NOT NULL,
	[PadLength] [tinyint] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__NumberSeq__NextN__625A9A57]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[NumberSequences] ADD  DEFAULT ((1)) FOR [NextNumber]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__NumberSeq__PadLe__634EBE90]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[NumberSequences] ADD  DEFAULT ((6)) FOR [PadLength]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[OfficialHolidays]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[OfficialHolidays](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[NameAr] [nvarchar](120) NOT NULL,
	[NameEn] [nvarchar](120) NULL,
	[HolidayDate] [date] NOT NULL,
	[IsRecurring] [bit] NOT NULL,
	[Notes] [nvarchar](300) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__OfficialH__IsRec__6B79F03D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OfficialHolidays] ADD  DEFAULT ((0)) FOR [IsRecurring]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[OpeningBalanceControls]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[OpeningBalanceControls](
	[CompanyID] [int] NOT NULL,
	[CutoffDate] [datetime2](7) NULL,
	[Finalized] [bit] NOT NULL,
	[FinalizedAt] [datetime2](7) NULL,
	[FinalizedBy] [nvarchar](450) NULL,
PRIMARY KEY CLUSTERED 
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__OpeningBa__Final__63D8CE75]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpeningBalanceControls] ADD  DEFAULT ((0)) FOR [Finalized]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[OpeningBalances]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[OpeningBalances](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Kind] [nvarchar](20) NOT NULL,
	[EntityRef] [nvarchar](80) NOT NULL,
	[Description] [nvarchar](200) NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[JournalEntryId] [int] NULL,
	[CutoffDate] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__OpeningBa__Amoun__60FC61CA]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpeningBalances] ADD  DEFAULT ((0)) FOR [Amount]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Opportunities]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Opportunities](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Title] [nvarchar](200) NOT NULL,
	[CustomerId] [int] NULL,
	[LeadId] [int] NULL,
	[Stage] [nvarchar](40) NOT NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[Probability] [int] NOT NULL,
	[ExpectedCloseDate] [datetime2](7) NULL,
	[Notes] [nvarchar](1000) NULL,
	[QuotationId] [int] NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CampaignId] [int] NULL,
	[OwnerEmployeeId] [int] NULL,
	[AccountId] [int] NULL,
	[PipelineId] [int] NULL,
	[StageId] [int] NULL,
	[WinLossReason] [nvarchar](300) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Opportunities]') AND name = N'IX_Opportunities_Company')
CREATE NONCLUSTERED INDEX [IX_Opportunities_Company] ON [dbo].[Opportunities]
(
	[CompanyID] ASC,
	[Stage] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__Stage__5649C92D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Opportunities] ADD  DEFAULT ('Prospecting') FOR [Stage]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__Amoun__573DED66]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Opportunities] ADD  DEFAULT ((0)) FOR [Amount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__Proba__5832119F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Opportunities] ADD  DEFAULT ((0)) FOR [Probability]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[OpportunityProducts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[OpportunityProducts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[OpportunityId] [int] NOT NULL,
	[ItemId] [int] NULL,
	[ItemDescription] [nvarchar](300) NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountPercent] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[OpportunityProducts]') AND name = N'IX_OpportunityProducts_Opp')
CREATE NONCLUSTERED INDEX [IX_OpportunityProducts_Opp] ON [dbo].[OpportunityProducts]
(
	[CompanyID] ASC,
	[OpportunityId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportunity__Qty__7A8729A3]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpportunityProducts] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__UnitP__7B7B4DDC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpportunityProducts] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__Disco__7C6F7215]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpportunityProducts] ADD  DEFAULT ((0)) FOR [DiscountPercent]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Opportuni__LineT__7D63964E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[OpportunityProducts] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PaymentAllocations]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PaymentAllocations](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PaymentId] [int] NOT NULL,
	[PurchaseInvoiceId] [int] NOT NULL,
	[ForeignAmount] [decimal](19, 4) NOT NULL,
	[InvoiceRate] [decimal](19, 8) NOT NULL,
	[PaymentRate] [decimal](19, 8) NOT NULL,
	[ApBase] [decimal](19, 4) NOT NULL,
	[FxDiff] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PaymentAllocations]') AND name = N'IX_PaymentAllocations_Inv')
CREATE NONCLUSTERED INDEX [IX_PaymentAllocations_Inv] ON [dbo].[PaymentAllocations]
(
	[PurchaseInvoiceId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PaymentAllocations]') AND name = N'IX_PaymentAllocations_Py')
CREATE NONCLUSTERED INDEX [IX_PaymentAllocations_Py] ON [dbo].[PaymentAllocations]
(
	[PaymentId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Payments]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Payments](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PaymentNo] [nvarchar](40) NULL,
	[PaymentDate] [date] NOT NULL,
	[VendorId] [int] NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[CurrencyId] [int] NULL,
	[Method] [nvarchar](20) NOT NULL,
	[CashAccountId] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[AmountBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payments__Method__19AACF41]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payments] ADD  DEFAULT ('Cash') FOR [Method]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payments__Status__1A9EF37A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payments] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PayrollSettings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PayrollSettings](
	[CompanyID] [int] NOT NULL,
	[PersonalExemptionAnnual] [decimal](19, 4) NOT NULL,
	[TaxBaseExcludesEmployeeSI] [bit] NOT NULL,
	[SiMinMonthly] [decimal](19, 4) NOT NULL,
	[SiMaxMonthly] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
	[GratuityDaysPerYear] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PayrollSe__Perso__09FE775D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PayrollSettings] ADD  DEFAULT ((0)) FOR [PersonalExemptionAnnual]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PayrollSe__TaxBa__0AF29B96]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PayrollSettings] ADD  DEFAULT ((1)) FOR [TaxBaseExcludesEmployeeSI]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PayrollSe__SiMin__0BE6BFCF]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PayrollSettings] ADD  DEFAULT ((0)) FOR [SiMinMonthly]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PayrollSe__SiMax__0CDAE408]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PayrollSettings] ADD  DEFAULT ((0)) FOR [SiMaxMonthly]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF_PayrollSettings_Gratuity]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PayrollSettings] ADD  CONSTRAINT [DF_PayrollSettings_Gratuity]  DEFAULT ((21)) FOR [GratuityDaysPerYear]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PayrollTaxBrackets]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PayrollTaxBrackets](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Ordinal] [int] NOT NULL,
	[FromAmount] [decimal](19, 4) NOT NULL,
	[ToAmount] [decimal](19, 4) NULL,
	[Rate] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PayrollTaxBrackets]') AND name = N'UX_PayrollTaxBrackets_Company_Ord')
CREATE UNIQUE NONCLUSTERED INDEX [UX_PayrollTaxBrackets_Company_Ord] ON [dbo].[PayrollTaxBrackets]
(
	[CompanyID] ASC,
	[Ordinal] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Payslips]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Payslips](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Year] [int] NOT NULL,
	[Month] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[EmployeeName] [nvarchar](200) NOT NULL,
	[CostCenterId] [int] NULL,
	[BaseSalary] [decimal](19, 4) NOT NULL,
	[Allowances] [decimal](19, 4) NOT NULL,
	[OvertimePay] [decimal](19, 4) NOT NULL,
	[GrossEarnings] [decimal](19, 4) NOT NULL,
	[LateMinutes] [int] NOT NULL,
	[LatePenalty] [decimal](19, 4) NOT NULL,
	[AbsentDays] [int] NOT NULL,
	[AbsencePenalty] [decimal](19, 4) NOT NULL,
	[SiEmployee] [decimal](19, 4) NOT NULL,
	[SiCompany] [decimal](19, 4) NOT NULL,
	[Tax] [decimal](19, 4) NOT NULL,
	[Net] [decimal](19, 4) NOT NULL,
	[JournalEntryId] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [UX_Payslip_Emp_Period] UNIQUE NONCLUSTERED 
(
	[CompanyID] ASC,
	[Year] ASC,
	[Month] ASC,
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__BaseSa__77DFC722]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [BaseSalary]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Allowa__78D3EB5B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [Allowances]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Overti__79C80F94]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [OvertimePay]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__GrossE__7ABC33CD]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [GrossEarnings]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__LateMi__7BB05806]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [LateMinutes]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__LatePe__7CA47C3F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [LatePenalty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Absent__7D98A078]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [AbsentDays]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Absenc__7E8CC4B1]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [AbsencePenalty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__SiEmpl__7F80E8EA]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [SiEmployee]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__SiComp__00750D23]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [SiCompany]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Tax__0169315C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [Tax]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Payslips__Net__025D5595]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Payslips] ADD  DEFAULT ((0)) FOR [Net]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Policies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Policies](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[NameAr] [nvarchar](max) NULL,
	[NameEn] [nvarchar](max) NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
	[Notes] [nvarchar](max) NULL,
	[ApprovalLevels] [int] NULL,
 CONSTRAINT [PK_Policies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PolicyAssignments](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LeavePolicyTypeID] [int] NOT NULL,
	[EmployeeID] [int] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
 CONSTRAINT [PK_PolicyAssignments] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]') AND name = N'IX_PolicyAssignments_EmployeeID')
CREATE NONCLUSTERED INDEX [IX_PolicyAssignments_EmployeeID] ON [dbo].[PolicyAssignments]
(
	[EmployeeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]') AND name = N'IX_PolicyAssignments_LeavePolicyTypeID')
CREATE NONCLUSTERED INDEX [IX_PolicyAssignments_LeavePolicyTypeID] ON [dbo].[PolicyAssignments]
(
	[LeavePolicyTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PostingRules]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PostingRules](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[SourceType] [nvarchar](40) NOT NULL,
	[ComponentCode] [nvarchar](60) NOT NULL,
	[DebitAccountId] [int] NULL,
	[CreditAccountId] [int] NULL,
	[CostCenterSource] [nvarchar](40) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PriceListLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PriceListLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[PriceListId] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[MinQty] [decimal](19, 4) NOT NULL,
	[UnitPrice] [decimal](19, 4) NULL,
	[DiscountPercent] [decimal](19, 4) NOT NULL,
	[ValidFrom] [datetime2](7) NULL,
	[ValidTo] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PriceListLines]') AND name = N'IX_PriceListLines_Item')
CREATE NONCLUSTERED INDEX [IX_PriceListLines_Item] ON [dbo].[PriceListLines]
(
	[ItemId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PriceListLines]') AND name = N'IX_PriceListLines_List')
CREATE NONCLUSTERED INDEX [IX_PriceListLines_List] ON [dbo].[PriceListLines]
(
	[PriceListId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PriceList__MinQt__4EA8A765]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PriceListLines] ADD  DEFAULT ((1)) FOR [MinQty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PriceList__Disco__4F9CCB9E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PriceListLines] ADD  DEFAULT ((0)) FOR [DiscountPercent]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PriceLists]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PriceLists](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NULL,
	[Segment] [nvarchar](80) NULL,
	[CurrencyId] [int] NULL,
	[Priority] [int] NOT NULL,
	[IsDefault] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[ValidFrom] [datetime2](7) NULL,
	[ValidTo] [datetime2](7) NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CustomerId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PriceLists]') AND name = N'IX_PriceLists_Company')
CREATE NONCLUSTERED INDEX [IX_PriceLists_Company] ON [dbo].[PriceLists]
(
	[CompanyID] ASC,
	[IsActive] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PriceList__Prior__49E3F248]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PriceLists] ADD  DEFAULT ((0)) FOR [Priority]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PriceList__IsDef__4AD81681]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PriceLists] ADD  DEFAULT ((0)) FOR [IsDefault]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PriceList__IsAct__4BCC3ABA]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PriceLists] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Projects]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Projects](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Projects__IsActi__6EC0713C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Projects] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseInvoiceLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseInvoiceLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[PurchaseInvoiceId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemDescription] [nvarchar](500) NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](7, 4) NOT NULL,
	[ExpenseAccountId] [int] NOT NULL,
	[CostCenterId] [int] NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[ItemId] [int] NULL,
	[WarehouseId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseInv__Qty__0F2D40CE]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoiceLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__UnitP__10216507]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoiceLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__Disco__11158940]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoiceLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__TaxRa__1209AD79]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoiceLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__LineT__12FDD1B2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoiceLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseInvoices]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseInvoices](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[InvoiceNo] [nvarchar](40) NULL,
	[InvoiceDate] [date] NOT NULL,
	[VendorId] [int] NOT NULL,
	[CurrencyId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[SubTotalBase] [decimal](19, 4) NULL,
	[TaxTotalBase] [decimal](19, 4) NULL,
	[GrandTotalBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__SubTo__09746778]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoices] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__TaxTo__0A688BB1]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoices] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__Grand__0B5CAFEA]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoices] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseI__Statu__0C50D423]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseInvoices] ADD  DEFAULT ('Draft') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseOrderLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseOrderLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[PurchaseOrderId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NULL,
	[ItemDescription] [nvarchar](300) NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](9, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[ReceivedQty] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseOrderLines]') AND name = N'IX_POLines_PO')
CREATE NONCLUSTERED INDEX [IX_POLines_PO] ON [dbo].[PurchaseOrderLines]
(
	[PurchaseOrderId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseOrd__Qty__0A338187]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__UnitP__0B27A5C0]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__Disco__0C1BC9F9]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__TaxRa__0D0FEE32]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__LineT__0E04126B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__Recei__0EF836A4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrderLines] ADD  DEFAULT ((0)) FOR [ReceivedQty]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseOrders]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseOrders](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[OrderNo] [nvarchar](40) NULL,
	[OrderDate] [datetime] NOT NULL,
	[ExpectedDate] [datetime] NULL,
	[VendorId] [int] NOT NULL,
	[WarehouseId] [int] NULL,
	[CurrencyId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseOrders]') AND name = N'IX_PurchaseOrders_Vendor')
CREATE NONCLUSTERED INDEX [IX_PurchaseOrders_Vendor] ON [dbo].[PurchaseOrders]
(
	[CompanyID] ASC,
	[VendorId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__SubTo__047AA831]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrders] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__TaxTo__056ECC6A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrders] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__Grand__0662F0A3]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrders] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseO__Statu__075714DC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseOrders] ADD  DEFAULT ('Draft') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseReturnLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseReturnLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[PurchaseReturnId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[ItemDescription] [nvarchar](400) NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[TaxRate] [decimal](19, 4) NOT NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseReturnLines]') AND name = N'IX_PurchaseReturnLines_Ret')
CREATE NONCLUSTERED INDEX [IX_PurchaseReturnLines_Ret] ON [dbo].[PurchaseReturnLines]
(
	[PurchaseReturnId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__ItemD__4336F4B9]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturnLines] ADD  DEFAULT ('') FOR [ItemDescription]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseRet__Qty__442B18F2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturnLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__TaxRa__451F3D2B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturnLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__UnitC__46136164]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturnLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__LineT__4707859D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturnLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseReturns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[PurchaseReturns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ReturnNo] [nvarchar](40) NULL,
	[ReturnDate] [datetime2](7) NOT NULL,
	[VendorId] [int] NOT NULL,
	[OriginalInvoiceId] [int] NULL,
	[WarehouseId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](40) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CurrencyId] [int] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[SubTotalBase] [decimal](19, 4) NULL,
	[TaxTotalBase] [decimal](19, 4) NULL,
	[GrandTotalBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[PurchaseReturns]') AND name = N'IX_PurchaseReturns_Company')
CREATE NONCLUSTERED INDEX [IX_PurchaseReturns_Company] ON [dbo].[PurchaseReturns]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__SubTo__3E723F9C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturns] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__TaxTo__3F6663D5]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturns] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__PurchaseR__Grand__405A880E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[PurchaseReturns] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[QuotationLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[QuotationLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[QuotationId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NULL,
	[ItemDescription] [nvarchar](400) NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[QuotationLines]') AND name = N'IX_QuotationLines_Quote')
CREATE NONCLUSTERED INDEX [IX_QuotationLines_Quote] ON [dbo].[QuotationLines]
(
	[QuotationId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__QuotationLi__Qty__2A6B46EF]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[QuotationLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__UnitP__2B5F6B28]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[QuotationLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__Disco__2C538F61]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[QuotationLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__TaxRa__2D47B39A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[QuotationLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__LineT__2E3BD7D3]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[QuotationLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Quotations]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Quotations](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[QuoteNo] [nvarchar](40) NULL,
	[QuoteDate] [datetime2](7) NOT NULL,
	[ValidUntil] [datetime2](7) NULL,
	[CustomerId] [int] NOT NULL,
	[WarehouseId] [int] NULL,
	[CurrencyId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](40) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[SalesOrderId] [int] NULL,
	[CreatedBy] [nvarchar](100) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Quotations]') AND name = N'IX_Quotations_Company')
CREATE NONCLUSTERED INDEX [IX_Quotations_Company] ON [dbo].[Quotations]
(
	[CompanyID] ASC,
	[Status] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__SubTo__25A691D2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Quotations] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__TaxTo__269AB60B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Quotations] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Quotation__Grand__278EDA44]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Quotations] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ReceiptAllocations]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[ReceiptAllocations](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ReceiptId] [int] NOT NULL,
	[SalesInvoiceId] [int] NOT NULL,
	[ForeignAmount] [decimal](19, 4) NOT NULL,
	[InvoiceRate] [decimal](19, 8) NOT NULL,
	[ReceiptRate] [decimal](19, 8) NOT NULL,
	[ArBase] [decimal](19, 4) NOT NULL,
	[FxDiff] [decimal](19, 4) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[ReceiptAllocations]') AND name = N'IX_ReceiptAllocations_Inv')
CREATE NONCLUSTERED INDEX [IX_ReceiptAllocations_Inv] ON [dbo].[ReceiptAllocations]
(
	[SalesInvoiceId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[ReceiptAllocations]') AND name = N'IX_ReceiptAllocations_Rc')
CREATE NONCLUSTERED INDEX [IX_ReceiptAllocations_Rc] ON [dbo].[ReceiptAllocations]
(
	[ReceiptId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Receipts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Receipts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ReceiptNo] [nvarchar](40) NULL,
	[ReceiptDate] [date] NOT NULL,
	[CustomerId] [int] NULL,
	[Amount] [decimal](19, 4) NOT NULL,
	[CurrencyId] [int] NULL,
	[Method] [nvarchar](20) NOT NULL,
	[CashAccountId] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[AmountBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Receipts__Method__15DA3E5D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Receipts] ADD  DEFAULT ('Cash') FOR [Method]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Receipts__Status__16CE6296]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Receipts] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalaryPolicies]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalaryPolicies](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[LeavePolicyTypeID] [int] NOT NULL,
	[Description] [nvarchar](max) NULL,
	[BaseSalary] [decimal](19, 4) NOT NULL,
	[HousingAllowance] [decimal](19, 4) NOT NULL,
	[TransportationAllowance] [decimal](19, 4) NOT NULL,
	[OtherAllowances] [decimal](19, 4) NOT NULL,
	[OvertimeRate] [decimal](19, 4) NOT NULL,
	[LatePenaltyPerMinute] [decimal](19, 4) NOT NULL,
	[AbsencePenaltyPerDay] [decimal](19, 4) NOT NULL,
	[SocialInsuranceEmployeeShare] [decimal](19, 4) NOT NULL,
	[SocialInsuranceCompanyShare] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](19, 4) NOT NULL,
	[IsTaxApplicable] [bit] NOT NULL,
	[PaymentDay] [int] NOT NULL,
	[PaymentMethod] [nvarchar](max) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
 CONSTRAINT [PK_SalaryPolicies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[SalaryPolicies]') AND name = N'IX_SalaryPolicies_LeavePolicyTypeID')
CREATE NONCLUSTERED INDEX [IX_SalaryPolicies_LeavePolicyTypeID] ON [dbo].[SalaryPolicies]
(
	[LeavePolicyTypeID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesInvoiceLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesInvoiceLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[SalesInvoiceId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemDescription] [nvarchar](500) NOT NULL,
	[ItemCode] [nvarchar](60) NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](7, 4) NOT NULL,
	[RevenueAccountId] [int] NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[ItemId] [int] NULL,
	[WarehouseId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvoic__Qty__02C769E9]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoiceLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__UnitP__03BB8E22]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoiceLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__Disco__04AFB25B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoiceLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__TaxRa__05A3D694]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoiceLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__LineT__0697FACD]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoiceLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesInvoices]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesInvoices](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[InvoiceNo] [nvarchar](40) NULL,
	[InvoiceDate] [date] NOT NULL,
	[CustomerId] [int] NOT NULL,
	[CurrencyId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[EtaUuid] [nvarchar](100) NULL,
	[EtaStatus] [nvarchar](30) NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[SubTotalBase] [decimal](19, 4) NULL,
	[TaxTotalBase] [decimal](19, 4) NULL,
	[GrandTotalBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__SubTo__7D0E9093]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoices] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__TaxTo__7E02B4CC]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoices] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__Grand__7EF6D905]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoices] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesInvo__Statu__7FEAFD3E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesInvoices] ADD  DEFAULT ('Draft') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesOrderLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesOrderLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[SalesOrderId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NULL,
	[ItemDescription] [nvarchar](300) NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](9, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[DeliveredQty] [decimal](19, 4) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[SalesOrderLines]') AND name = N'IX_SOLines_SO')
CREATE NONCLUSTERED INDEX [IX_SOLines_SO] ON [dbo].[SalesOrderLines]
(
	[SalesOrderId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrderL__Qty__2022C2A6]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__UnitP__2116E6DF]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__Disco__220B0B18]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__TaxRa__22FF2F51]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__LineT__23F3538A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__Deliv__24E777C3]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrderLines] ADD  DEFAULT ((0)) FOR [DeliveredQty]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesOrders]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesOrders](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[OrderNo] [nvarchar](40) NULL,
	[OrderDate] [datetime] NOT NULL,
	[ExpectedDate] [datetime] NULL,
	[CustomerId] [int] NOT NULL,
	[WarehouseId] [int] NULL,
	[CurrencyId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[SalesOrders]') AND name = N'IX_SalesOrders_Customer')
CREATE NONCLUSTERED INDEX [IX_SalesOrders_Customer] ON [dbo].[SalesOrders]
(
	[CompanyID] ASC,
	[CustomerId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__SubTo__1A69E950]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrders] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__TaxTo__1B5E0D89]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrders] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__Grand__1C5231C2]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrders] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesOrde__Statu__1D4655FB]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesOrders] ADD  DEFAULT ('Draft') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesReturnLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesReturnLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[SalesReturnId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemDescription] [nvarchar](400) NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UnitPrice] [decimal](19, 4) NOT NULL,
	[DiscountAmount] [decimal](19, 4) NOT NULL,
	[TaxRate] [decimal](19, 4) NOT NULL,
	[RevenueAccountId] [int] NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[ItemId] [int] NULL,
	[WarehouseId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[SalesReturnLines]') AND name = N'IX_SalesReturnLines_Ret')
CREATE NONCLUSTERED INDEX [IX_SalesReturnLines_Ret] ON [dbo].[SalesReturnLines]
(
	[SalesReturnId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__ItemD__35DCF99B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ('') FOR [ItemDescription]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesReturn__Qty__36D11DD4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__UnitP__37C5420D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((0)) FOR [UnitPrice]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__Disco__38B96646]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((0)) FOR [DiscountAmount]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__TaxRa__39AD8A7F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((0)) FOR [TaxRate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__Reven__3AA1AEB8]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((0)) FOR [RevenueAccountId]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__LineT__3B95D2F1]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturnLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SalesReturns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SalesReturns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ReturnNo] [nvarchar](40) NULL,
	[ReturnDate] [datetime2](7) NOT NULL,
	[CustomerId] [int] NOT NULL,
	[OriginalInvoiceId] [int] NULL,
	[WarehouseId] [int] NULL,
	[SubTotal] [decimal](19, 4) NOT NULL,
	[TaxTotal] [decimal](19, 4) NOT NULL,
	[GrandTotal] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](40) NOT NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[CurrencyId] [int] NULL,
	[ExchangeRate] [decimal](19, 8) NULL,
	[SubTotalBase] [decimal](19, 4) NULL,
	[TaxTotalBase] [decimal](19, 4) NULL,
	[GrandTotalBase] [decimal](19, 4) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[SalesReturns]') AND name = N'IX_SalesReturns_Company')
CREATE NONCLUSTERED INDEX [IX_SalesReturns_Company] ON [dbo].[SalesReturns]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__SubTo__3118447E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturns] ADD  DEFAULT ((0)) FOR [SubTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__TaxTo__320C68B7]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturns] ADD  DEFAULT ((0)) FOR [TaxTotal]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__SalesRetu__Grand__33008CF0]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[SalesReturns] ADD  DEFAULT ((0)) FOR [GrandTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockBalances]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockBalances](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[QtyOnHand] [decimal](19, 4) NOT NULL,
	[TotalValue] [decimal](19, 4) NOT NULL,
	[AvgCost] [decimal](19, 4) NOT NULL,
	[LastMovementAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockBalances]') AND name = N'UX_StockBalances_ItemWh')
CREATE UNIQUE NONCLUSTERED INDEX [UX_StockBalances_ItemWh] ON [dbo].[StockBalances]
(
	[CompanyID] ASC,
	[ItemId] ASC,
	[WarehouseId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockBala__QtyOn__7908F585]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockBalances] ADD  DEFAULT ((0)) FOR [QtyOnHand]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockBala__Total__79FD19BE]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockBalances] ADD  DEFAULT ((0)) FOR [TotalValue]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockBala__AvgCo__7AF13DF7]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockBalances] ADD  DEFAULT ((0)) FOR [AvgCost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockBatches]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockBatches](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[BatchNo] [nvarchar](80) NOT NULL,
	[ExpiryDate] [date] NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockBatches]') AND name = N'UX_StockBatches_ItemBatch')
CREATE UNIQUE NONCLUSTERED INDEX [UX_StockBatches_ItemBatch] ON [dbo].[StockBatches]
(
	[CompanyID] ASC,
	[ItemId] ASC,
	[BatchNo] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockCostLayers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockCostLayers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[BatchId] [int] NULL,
	[ReceiptDate] [datetime] NOT NULL,
	[QtyRemaining] [decimal](19, 4) NOT NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[SourceMovementId] [int] NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockCostLayers]') AND name = N'IX_StockCostLayers_ItemWh')
CREATE NONCLUSTERED INDEX [IX_StockCostLayers_ItemWh] ON [dbo].[StockCostLayers]
(
	[CompanyID] ASC,
	[ItemId] ASC,
	[WarehouseId] ASC,
	[ReceiptDate] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockCountLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockCountLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[StockCountId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[BookQty] [decimal](19, 4) NOT NULL,
	[CountedQty] [decimal](19, 4) NOT NULL,
	[DiffQty] [decimal](19, 4) NOT NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[DiffValue] [decimal](19, 4) NOT NULL,
	[AdjustmentMovementId] [int] NULL,
	[Reason] [nvarchar](40) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockCountLines]') AND name = N'IX_StockCountLines_C')
CREATE NONCLUSTERED INDEX [IX_StockCountLines_C] ON [dbo].[StockCountLines]
(
	[StockCountId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__BookQ__3CBF0154]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCountLines] ADD  DEFAULT ((0)) FOR [BookQty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__Count__3DB3258D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCountLines] ADD  DEFAULT ((0)) FOR [CountedQty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__DiffQ__3EA749C6]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCountLines] ADD  DEFAULT ((0)) FOR [DiffQty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__UnitC__3F9B6DFF]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCountLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__DiffV__408F9238]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCountLines] ADD  DEFAULT ((0)) FOR [DiffValue]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockCounts]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockCounts](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[CountNo] [nvarchar](40) NULL,
	[CountDate] [datetime] NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[TotalAdjValue] [decimal](19, 4) NOT NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockCounts]') AND name = N'IX_StockCounts_Co')
CREATE NONCLUSTERED INDEX [IX_StockCounts_Co] ON [dbo].[StockCounts]
(
	[CompanyID] ASC,
	[WarehouseId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__Statu__38EE7070]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCounts] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockCoun__Total__39E294A9]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockCounts] ADD  DEFAULT ((0)) FOR [TotalAdjValue]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockMovements]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockMovements](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[MovementNo] [nvarchar](40) NULL,
	[MovementDate] [datetime] NOT NULL,
	[ItemId] [int] NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[BinLocationId] [int] NULL,
	[BatchId] [int] NULL,
	[SerialNo] [nvarchar](80) NULL,
	[Direction] [smallint] NOT NULL,
	[QtyBase] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[QtyInUoM] [decimal](19, 4) NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[TotalCost] [decimal](19, 4) NOT NULL,
	[SourceType] [nvarchar](40) NULL,
	[SourceId] [int] NULL,
	[SourceLineId] [int] NULL,
	[JournalEntryId] [int] NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockMovements]') AND name = N'IX_StockMovements_ItemWh')
CREATE NONCLUSTERED INDEX [IX_StockMovements_ItemWh] ON [dbo].[StockMovements]
(
	[CompanyID] ASC,
	[ItemId] ASC,
	[WarehouseId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockMovements]') AND name = N'IX_StockMovements_Source')
CREATE NONCLUSTERED INDEX [IX_StockMovements_Source] ON [dbo].[StockMovements]
(
	[SourceType] ASC,
	[SourceId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockMove__UnitC__753864A1]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockMovements] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockMove__Total__762C88DA]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockMovements] ADD  DEFAULT ((0)) FOR [TotalCost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockSerials]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockSerials](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[SerialNo] [nvarchar](80) NOT NULL,
	[WarehouseId] [int] NULL,
	[Status] [nvarchar](20) NOT NULL,
	[LastMovementId] [int] NULL,
	[CreatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockSerials]') AND name = N'UX_StockSerials_ItemSerial')
CREATE UNIQUE NONCLUSTERED INDEX [UX_StockSerials_ItemSerial] ON [dbo].[StockSerials]
(
	[CompanyID] ASC,
	[ItemId] ASC,
	[SerialNo] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockSeri__Statu__019E3B86]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockSerials] ADD  DEFAULT ('InStock') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockTransferLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockTransferLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[StockTransferId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[LineTotal] [decimal](19, 4) NOT NULL,
	[BatchNo] [nvarchar](80) NULL,
	[SerialNo] [nvarchar](80) NULL,
	[OutMovementId] [int] NULL,
	[InMovementId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockTransferLines]') AND name = N'IX_TransferLines_T')
CREATE NONCLUSTERED INDEX [IX_TransferLines_T] ON [dbo].[StockTransferLines]
(
	[StockTransferId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockTransf__Qty__3429BB53]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockTransferLines] ADD  DEFAULT ((1)) FOR [Qty]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockTran__UnitC__351DDF8C]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockTransferLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockTran__LineT__361203C5]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockTransferLines] ADD  DEFAULT ((0)) FOR [LineTotal]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockTransfers]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockTransfers](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[TransferNo] [nvarchar](40) NULL,
	[TransferDate] [datetime] NOT NULL,
	[FromWarehouseId] [int] NOT NULL,
	[ToWarehouseId] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[TotalCost] [decimal](19, 4) NOT NULL,
	[Notes] [nvarchar](400) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime] NULL,
	[JournalEntryId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[StockTransfers]') AND name = N'IX_StockTransfers_Co')
CREATE NONCLUSTERED INDEX [IX_StockTransfers_Co] ON [dbo].[StockTransfers]
(
	[CompanyID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockTran__Statu__30592A6F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockTransfers] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockTran__Total__314D4EA8]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockTransfers] ADD  DEFAULT ((0)) FOR [TotalCost]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockWriteOffLines]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockWriteOffLines](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[StockWriteOffId] [int] NOT NULL,
	[LineNo] [int] NOT NULL,
	[ItemId] [int] NOT NULL,
	[Qty] [decimal](19, 4) NOT NULL,
	[UoMId] [int] NULL,
	[BatchNo] [nvarchar](60) NULL,
	[SerialNo] [nvarchar](60) NULL,
	[Reason] [nvarchar](40) NULL,
	[UnitCost] [decimal](19, 4) NOT NULL,
	[LineValue] [decimal](19, 4) NOT NULL,
	[MovementId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockWrit__UnitC__595B4002]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockWriteOffLines] ADD  DEFAULT ((0)) FOR [UnitCost]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockWrit__LineV__5A4F643B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockWriteOffLines] ADD  DEFAULT ((0)) FOR [LineValue]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[StockWriteOffs]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[StockWriteOffs](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[WriteOffNo] [nvarchar](40) NULL,
	[WriteOffDate] [datetime2](7) NOT NULL,
	[WarehouseId] [int] NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[Reason] [nvarchar](40) NULL,
	[TotalValue] [decimal](19, 4) NOT NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[JournalEntryId] [int] NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockWrit__Statu__558AAF1E]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockWriteOffs] ADD  DEFAULT ('Posted') FOR [Status]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__StockWrit__Total__567ED357]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[StockWriteOffs] ADD  DEFAULT ((0)) FOR [TotalValue]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SystemForms]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[SystemForms](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[NameAr] [nvarchar](max) NOT NULL,
	[NameEn] [nvarchar](max) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedAt] [datetime2](7) NULL,
	[updatedBy] [int] NULL,
	[UpdatedAt] [datetime2](7) NULL,
 CONSTRAINT [PK_SystemForms] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[TaxCodes]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[TaxCodes](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[Kind] [nvarchar](10) NOT NULL,
	[Rate] [decimal](7, 4) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[IsDefault] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__TaxCodes__Kind__3FD07829]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[TaxCodes] ADD  DEFAULT ('VAT') FOR [Kind]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__TaxCodes__Rate__40C49C62]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[TaxCodes] ADD  DEFAULT ((0)) FOR [Rate]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__TaxCodes__IsActi__41B8C09B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[TaxCodes] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__TaxCodes__IsDefa__42ACE4D4]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[TaxCodes] ADD  DEFAULT ((0)) FOR [IsDefault]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[UnitsOfMeasure]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[UnitsOfMeasure](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](20) NOT NULL,
	[Name] [nvarchar](100) NOT NULL,
	[NameEn] [nvarchar](100) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__UnitsOfMe__IsAct__56B3DD81]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[UnitsOfMeasure] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[UoMConversions]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[UoMConversions](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[ItemId] [int] NOT NULL,
	[FromUoMId] [int] NOT NULL,
	[ToUoMId] [int] NOT NULL,
	[Factor] [decimal](19, 6) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[VatReturns]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[VatReturns](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[PeriodStart] [date] NOT NULL,
	[PeriodEnd] [date] NOT NULL,
	[OutputVat] [decimal](19, 4) NOT NULL,
	[InputVat] [decimal](19, 4) NOT NULL,
	[NetDue] [decimal](19, 4) NOT NULL,
	[Status] [nvarchar](20) NOT NULL,
	[JournalEntryId] [int] NULL,
	[FiledAt] [datetime2](7) NULL,
	[Notes] [nvarchar](500) NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__VatReturn__Outpu__4589517F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[VatReturns] ADD  DEFAULT ((0)) FOR [OutputVat]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__VatReturn__Input__467D75B8]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[VatReturns] ADD  DEFAULT ((0)) FOR [InputVat]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__VatReturn__NetDu__477199F1]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[VatReturns] ADD  DEFAULT ((0)) FOR [NetDue]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__VatReturn__Statu__4865BE2A]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[VatReturns] ADD  DEFAULT ('Filed') FOR [Status]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Vendors]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Vendors](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Name] [nvarchar](250) NOT NULL,
	[NameEn] [nvarchar](250) NULL,
	[TaxRegNo] [nvarchar](50) NULL,
	[Address] [nvarchar](500) NULL,
	[ControlAccountId] [int] NOT NULL,
	[CurrencyId] [int] NULL,
	[PaymentTermsDays] [int] NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
	[Phone] [nvarchar](50) NULL,
	[Email] [nvarchar](150) NULL,
	[ContactPerson] [nvarchar](150) NULL,
	[Segment] [nvarchar](80) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Vendors__IsActiv__7A3223E8]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Vendors] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Warehouses]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[Warehouses](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[Code] [nvarchar](40) NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[NameEn] [nvarchar](200) NOT NULL,
	[WarehouseType] [nvarchar](20) NOT NULL,
	[BranchHierarchicalId] [int] NULL,
	[KeeperEmployeeId] [int] NULL,
	[AllowNegativeStock] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [nvarchar](450) NULL,
	[CreatedAt] [datetime2](7) NULL,
	[ModifiedBy] [nvarchar](450) NULL,
	[ModifiedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
SET ANSI_PADDING ON

GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Warehouses]') AND name = N'UX_Warehouses_Company_Code')
CREATE UNIQUE NONCLUSTERED INDEX [UX_Warehouses_Company_Code] ON [dbo].[Warehouses]
(
	[CompanyID] ASC,
	[Code] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Warehouse__Wareh__640DD89F]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Warehouses] ADD  DEFAULT ('Main') FOR [WarehouseType]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Warehouse__Allow__6501FCD8]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Warehouses] ADD  DEFAULT ((0)) FOR [AllowNegativeStock]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__Warehouse__IsAct__65F62111]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[Warehouses] ADD  DEFAULT ((1)) FOR [IsActive]
END

GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[YearEndClosings]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[YearEndClosings](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[CompanyID] [int] NOT NULL,
	[FiscalYearId] [int] NOT NULL,
	[CloseDate] [date] NOT NULL,
	[TotalRevenue] [decimal](19, 4) NOT NULL,
	[TotalExpense] [decimal](19, 4) NOT NULL,
	[NetResult] [decimal](19, 4) NOT NULL,
	[RetainedEarningsAccountId] [int] NOT NULL,
	[JournalEntryId] [int] NULL,
	[Status] [nvarchar](20) NOT NULL,
	[CreatedAt] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
END
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__YearEndCl__Total__50FB042B]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[YearEndClosings] ADD  DEFAULT ((0)) FOR [TotalRevenue]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__YearEndCl__Total__51EF2864]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[YearEndClosings] ADD  DEFAULT ((0)) FOR [TotalExpense]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__YearEndCl__NetRe__52E34C9D]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[YearEndClosings] ADD  DEFAULT ((0)) FOR [NetResult]
END

GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DF__YearEndCl__Statu__53D770D6]') AND type = 'D')
BEGIN
ALTER TABLE [dbo].[YearEndClosings] ADD  DEFAULT ('Closed') FOR [Status]
END

GO
