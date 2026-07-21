CREATE SCHEMA IF NOT EXISTS vibebase_control;

CREATE TABLE vibebase_control."Workspaces" (
    "Id" uuid NOT NULL,
    "Name" text NOT NULL,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "Tier" text NOT NULL DEFAULT 'free',
    CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
    CONSTRAINT "UQ_Workspaces_Name" UNIQUE ("Name"),
    CONSTRAINT "CK_Workspaces_Status" CHECK ("Status" IN ('active', 'suspended')),
    CONSTRAINT "CK_Workspaces_Tier" CHECK ("Tier" IN ('free', 'pro'))
);

CREATE TABLE vibebase_control."Members" (
    "Id" uuid NOT NULL,
    "WorkspaceId" uuid NOT NULL,
    "SubjectId" text NOT NULL,
    "Kind" text NOT NULL,
    CONSTRAINT "PK_Members" PRIMARY KEY ("Id"),
    CONSTRAINT "UQ_Members_Workspace_Subject" UNIQUE ("WorkspaceId", "SubjectId"),
    CONSTRAINT "CK_Members_Kind" CHECK ("Kind" IN ('admin', 'builder')),
    CONSTRAINT "FK_Members_Workspaces" FOREIGN KEY ("WorkspaceId")
        REFERENCES vibebase_control."Workspaces" ("Id") ON DELETE CASCADE
);

CREATE TABLE vibebase_control."Apps" (
    "Id" uuid NOT NULL,
    "WorkspaceId" uuid NOT NULL,
    "Target" text NOT NULL,
    CONSTRAINT "PK_Apps" PRIMARY KEY ("Id"),
    CONSTRAINT "UQ_Apps_Workspace_Target" UNIQUE ("WorkspaceId", "Target"),
    CONSTRAINT "FK_Apps_Workspaces" FOREIGN KEY ("WorkspaceId")
        REFERENCES vibebase_control."Workspaces" ("Id") ON DELETE CASCADE
);

CREATE TABLE vibebase_control."Resources" (
    "Id" uuid NOT NULL,
    "AppId" uuid NOT NULL,
    "Type" text NOT NULL,
    "ExternalRef" text NOT NULL,
    "ProvisionedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_Resources" PRIMARY KEY ("Id"),
    CONSTRAINT "UQ_Resources_App_ExternalRef" UNIQUE ("AppId", "ExternalRef"),
    CONSTRAINT "FK_Resources_Apps" FOREIGN KEY ("AppId")
        REFERENCES vibebase_control."Apps" ("Id") ON DELETE CASCADE
);
