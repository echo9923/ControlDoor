-- Stage 1-4 integration seed database for ControlDoor.
-- 与另外四个脚本（初始化_门禁设备系统用户与数据库登录.sql、
-- 专项_20260105_门禁进出记录表V2.sql、专项_20260309_设备操作重试状态表.sql、
-- 专项_人脸事件日志与断点表.sql）在表结构、索引、触发器、账号上完全等价。
-- 本脚本把库表与账号一次性建好；设备主数据仍从 Configuration/devices.json 加载。
--
-- 账号说明：本脚本建立的登录账号为 admin_user / operator_user /
-- monitor_user / integration_user（与初始化脚本一致），不包含 door_user。
-- 因此 appsettings.json 的连接串必须改用其中一个账号及其初始密码。

USE master;
GO

-- =====================================================
-- 数据库：与初始化脚本等价（含 DROP DATABASE 行为，幂等重置）
-- =====================================================
IF EXISTS (SELECT name FROM sys.databases WHERE name = N'ruoyi-vue-pro')
BEGIN
    DECLARE @kill_sessions NVARCHAR(MAX) = N'';

    SELECT @kill_sessions = @kill_sessions + N'KILL ' + CAST(session_id AS NVARCHAR(10)) + N';'
    FROM sys.dm_exec_sessions
    WHERE database_id = DB_ID(N'ruoyi-vue-pro')
      AND session_id <> @@SPID;

    IF (@kill_sessions <> N'')
    BEGIN
        EXEC sp_executesql @kill_sessions;
    END

    ALTER DATABASE [ruoyi-vue-pro] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [ruoyi-vue-pro];
END
GO

CREATE DATABASE [ruoyi-vue-pro]
COLLATE Chinese_PRC_CI_AS;
GO

USE [ruoyi-vue-pro];
GO

/* ========== 人员表 system_users ========== */
-- 与初始化脚本等价：无默认值约束命名、无 DF_xxx 约束、保留 AFTER UPDATE 触发器
IF OBJECT_ID(N'dbo.system_users', N'U') IS NOT NULL
    DROP TABLE dbo.system_users;
GO

CREATE TABLE dbo.system_users (
    id                    BIGINT        IDENTITY(1,1) NOT NULL,
    username              NVARCHAR(30)  NOT NULL,
    [password]            NVARCHAR(100) NOT NULL DEFAULT N'',
    nickname              NVARCHAR(30)  NOT NULL,
    remark                NVARCHAR(500) NULL,
    dept_id               BIGINT        NULL,
    post_ids              NVARCHAR(255) NULL,
    email                 NVARCHAR(50)  NULL DEFAULT N'',
    mobile                NVARCHAR(11)  NULL DEFAULT 0,
    sex                   TINYINT       NULL DEFAULT 0,
    avatar                NVARCHAR(512) NULL DEFAULT N'',
    status                TINYINT       NOT NULL DEFAULT 0,
    access_permission     TINYINT       NOT NULL DEFAULT 2,
    last_synced_level     TINYINT       NULL,
    permission_updated_at DATETIME2(3)  NULL,
    last_synced_at        DATETIME2(3)  NULL,
    login_ip              NVARCHAR(50)  NULL DEFAULT N'',
    login_date            DATETIME2(3)  NULL,
    creator               NVARCHAR(64)  NULL DEFAULT N'',
    create_time           DATETIME2(3)  NOT NULL DEFAULT SYSDATETIME(),
    updater               NVARCHAR(64)  NULL DEFAULT N'',
    update_time           DATETIME2(3)  NOT NULL DEFAULT SYSDATETIME(),
    deleted               BIT           NOT NULL DEFAULT 0,
    tenant_id             BIGINT        NOT NULL DEFAULT 1,
    CONSTRAINT PK_system_users PRIMARY KEY CLUSTERED (id),
    CONSTRAINT UQ_system_users_username UNIQUE (username)
);
GO

CREATE INDEX idx_system_users_username   ON dbo.system_users(username);
GO
CREATE INDEX idx_system_users_status     ON dbo.system_users(status);
GO
CREATE INDEX idx_system_users_permission ON dbo.system_users(access_permission);
GO
CREATE INDEX idx_system_users_deleted    ON dbo.system_users(deleted);
GO

CREATE TRIGGER trg_system_users_update
ON dbo.system_users
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE su
    SET update_time = SYSDATETIME()
    FROM dbo.system_users AS su
    INNER JOIN inserted AS i ON su.id = i.id;
END;
GO

EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'统一账号体系人员信息表（含门禁扩展字段）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE',  @level1name = N'system_users';
GO

/* ========== 进出记录表 attendance_gate_v2 ========== */
-- 与专项_20260105_门禁进出记录表V2.sql 完全等价（含 2 个查询索引）
IF OBJECT_ID(N'[dbo].[attendance_gate_v2]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[attendance_gate_v2] (
      [auto_id] bigint IDENTITY(1,1) NOT NULL,
      [id] bigint NOT NULL,
      [username] nvarchar(30) COLLATE Chinese_PRC_CI_AS NOT NULL,
      [nickname] nvarchar(50) COLLATE Chinese_PRC_CI_AS NULL,
      [record_datetime] datetime2(7) NOT NULL,
      [record_date] date NOT NULL,
      [record_time] time(7) NOT NULL,
      [direction] tinyint NOT NULL,
      [device_name] nvarchar(100) COLLATE Chinese_PRC_CI_AS NULL,
      [device_sn] nvarchar(100) COLLATE Chinese_PRC_CI_AS NULL,
      [card_no] nvarchar(64) COLLATE Chinese_PRC_CI_AS NULL,
      [snapshot_path] nvarchar(255) COLLATE Chinese_PRC_CI_AS NULL,
      [raw_payload] varchar(max) COLLATE Chinese_PRC_CI_AS NULL,
      [event_type] tinyint NULL,
      [process_status] tinyint NOT NULL,
      [process_message] nvarchar(255) COLLATE Chinese_PRC_CI_AS NULL,
      [processed_at] datetime2(7) NULL,
      [creator] nvarchar(64) COLLATE Chinese_PRC_CI_AS NULL,
      [create_time] datetime2(7) NOT NULL,
      [updater] nvarchar(64) COLLATE Chinese_PRC_CI_AS NULL,
      [update_time] datetime2(7) NOT NULL,
      [deleted] varchar(1) COLLATE Chinese_PRC_CI_AS NOT NULL,
      [tenant_id] bigint NOT NULL
    )
    ON [PRIMARY];

    ALTER TABLE [dbo].[attendance_gate_v2]
        ADD CONSTRAINT [PK_attendance_gate_v2] PRIMARY KEY CLUSTERED ([auto_id])
        WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
        ON [PRIMARY];

    CREATE UNIQUE NONCLUSTERED INDEX [ux_gate_v2_id]
        ON [dbo].[attendance_gate_v2] ([id] ASC)
        ON [PRIMARY];

    CREATE NONCLUSTERED INDEX [idx_gate_v2_user_time]
        ON [dbo].[attendance_gate_v2] ([username] ASC, [record_datetime] ASC)
        ON [PRIMARY];

    CREATE NONCLUSTERED INDEX [idx_gate_v2_status_date]
        ON [dbo].[attendance_gate_v2] ([process_status] ASC, [record_date] ASC)
        ON [PRIMARY];
END
GO

/* ========== 设备操作重试状态表 device_operation_retry_states ========== */
-- 与专项_20260309_设备操作重试状态表.sql 完全等价（含版本、租约列和 3 个性能索引）
IF OBJECT_ID(N'[dbo].[device_operation_retry_states]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[device_operation_retry_states]
    (
        [id] BIGINT IDENTITY(1,1) NOT NULL,
        [intent_version] BIGINT NOT NULL CONSTRAINT [DF_device_operation_retry_states_intent_version] DEFAULT ((1)),
        [claim_token] NVARCHAR(64) NULL,
        [claim_until] DATETIME2(0) NULL,
        [device_id] INT NOT NULL,
        [employee_id] NVARCHAR(64) NOT NULL,
        [permission_level] INT NULL,
        [permission_payload] NVARCHAR(MAX) NULL,
        [permission_pending] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_permission_pending] DEFAULT ((0)),
        [permission_sync_completion_blocked] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_permission_sync_completion_blocked] DEFAULT ((0)),
        [person_payload] NVARCHAR(MAX) NULL,
        [person_pending] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_person_pending] DEFAULT ((0)),
        [face_payload] NVARCHAR(MAX) NULL,
        [face_pending] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_face_pending] DEFAULT ((0)),
        [delete_person_pending] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_delete_person_pending] DEFAULT ((0)),
        [delete_face_pending] BIT NOT NULL CONSTRAINT [DF_device_operation_retry_states_delete_face_pending] DEFAULT ((0)),
        [attempt_count] INT NOT NULL CONSTRAINT [DF_device_operation_retry_states_attempt_count] DEFAULT ((0)),
        [next_retry_at] DATETIME2(0) NULL,
        [last_error] NVARCHAR(2000) NULL,
        [last_attempt_at] DATETIME2(0) NULL,
        [exhausted_at] DATETIME2(0) NULL,
        [created_at] DATETIME2(0) NOT NULL CONSTRAINT [DF_device_operation_retry_states_created_at] DEFAULT (SYSDATETIME()),
        [updated_at] DATETIME2(0) NOT NULL CONSTRAINT [DF_device_operation_retry_states_updated_at] DEFAULT (SYSDATETIME()),
        CONSTRAINT [PK_device_operation_retry_states] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [UQ_device_operation_retry_states_device_employee] UNIQUE NONCLUSTERED ([device_id] ASC, [employee_id] ASC)
    );
END
GO

IF COL_LENGTH(N'dbo.device_operation_retry_states', N'permission_payload') IS NULL
BEGIN
    ALTER TABLE [dbo].[device_operation_retry_states]
        ADD [permission_payload] NVARCHAR(MAX) NULL;
END
GO

-- 与专项脚本等价：补齐 permission_sync_completion_blocked 列（若表已存在且缺列）
-- 旧表升级必须保持历史行为：新增列默认 0；只有新的权限补偿意图会显式置 1。
IF COL_LENGTH(N'dbo.device_operation_retry_states', N'permission_sync_completion_blocked') IS NULL
BEGIN
    ALTER TABLE [dbo].[device_operation_retry_states]
        ADD [permission_sync_completion_blocked] BIT NOT NULL
            CONSTRAINT [DF_device_operation_retry_states_permission_sync_completion_blocked] DEFAULT ((0));
END
GO

-- 与专项脚本等价：补齐意图版本列（若表已存在且缺列）
IF COL_LENGTH(N'dbo.device_operation_retry_states', N'intent_version') IS NULL
BEGIN
    ALTER TABLE [dbo].[device_operation_retry_states]
        ADD [intent_version] BIGINT NOT NULL
            CONSTRAINT [DF_device_operation_retry_states_intent_version] DEFAULT ((1));
END
GO

-- 与专项脚本等价：补齐领取令牌列（若表已存在且缺列）
IF COL_LENGTH(N'dbo.device_operation_retry_states', N'claim_token') IS NULL
BEGIN
    ALTER TABLE [dbo].[device_operation_retry_states]
        ADD [claim_token] NVARCHAR(64) NULL;
END
GO

-- 与专项脚本等价：补齐领取租约截止时间列（若表已存在且缺列）
IF COL_LENGTH(N'dbo.device_operation_retry_states', N'claim_until') IS NULL
BEGIN
    ALTER TABLE [dbo].[device_operation_retry_states]
        ADD [claim_until] DATETIME2(0) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_device_operation_retry_states_next_retry_at'
      AND object_id = OBJECT_ID(N'[dbo].[device_operation_retry_states]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_device_operation_retry_states_next_retry_at]
        ON [dbo].[device_operation_retry_states] ([next_retry_at] ASC)
        INCLUDE ([device_id], [employee_id], [updated_at])
        WHERE [exhausted_at] IS NULL;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_device_operation_retry_states_employee_permission'
      AND object_id = OBJECT_ID(N'[dbo].[device_operation_retry_states]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_device_operation_retry_states_employee_permission]
        ON [dbo].[device_operation_retry_states] ([employee_id] ASC, [permission_pending] ASC)
        INCLUDE ([device_id], [updated_at]);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_device_operation_retry_states_exhausted_at'
      AND object_id = OBJECT_ID(N'[dbo].[device_operation_retry_states]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_device_operation_retry_states_exhausted_at]
        ON [dbo].[device_operation_retry_states] ([exhausted_at] ASC)
        INCLUDE ([device_id], [employee_id], [updated_at]);
END
GO

/* ========== 人脸事件断点表 face_event_checkpoint ========== */
-- 与专项_人脸事件日志与断点表.sql 完全等价
IF OBJECT_ID(N'dbo.face_event_checkpoint', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.face_event_checkpoint
    (
        DeviceIP      NVARCHAR(45) NOT NULL PRIMARY KEY,
        LastSerialNo  BIGINT       NOT NULL,
        LastEventTime DATETIME2(3) NOT NULL,
        UpdatedAt     DATETIME2(3) NOT NULL CONSTRAINT DF_face_event_checkpoint_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
END
GO

-- =====================================================
-- 数据库登录与用户：与初始化脚本完全等价
-- 建立 admin_user / operator_user / monitor_user / integration_user 四个账号
-- 不再建立 door_user；appsettings 连接串需改用以下任一账号
-- =====================================================
USE master;
GO

IF EXISTS (SELECT * FROM sys.server_principals WHERE name = N'admin_user')
    DROP LOGIN admin_user;
GO
IF EXISTS (SELECT * FROM sys.server_principals WHERE name = N'operator_user')
    DROP LOGIN operator_user;
GO
IF EXISTS (SELECT * FROM sys.server_principals WHERE name = N'monitor_user')
    DROP LOGIN monitor_user;
GO
IF EXISTS (SELECT * FROM sys.server_principals WHERE name = N'integration_user')
    DROP LOGIN integration_user;
GO

CREATE LOGIN admin_user
WITH PASSWORD = '123456',
     DEFAULT_DATABASE = [ruoyi-vue-pro],
     CHECK_EXPIRATION = OFF,
     CHECK_POLICY = OFF;
GO

CREATE LOGIN operator_user
WITH PASSWORD = 'Operator@123',
     DEFAULT_DATABASE = [ruoyi-vue-pro],
     CHECK_EXPIRATION = OFF,
     CHECK_POLICY = OFF;
GO

CREATE LOGIN monitor_user
WITH PASSWORD = 'Monitor@123',
     DEFAULT_DATABASE = [ruoyi-vue-pro],
     CHECK_EXPIRATION = OFF,
     CHECK_POLICY = OFF;
GO

CREATE LOGIN integration_user
WITH PASSWORD = 'Integration@123',
     DEFAULT_DATABASE = [ruoyi-vue-pro],
     CHECK_EXPIRATION = OFF,
     CHECK_POLICY = OFF;
GO

USE [ruoyi-vue-pro];
GO

CREATE USER admin_user FOR LOGIN admin_user;
GO
ALTER ROLE db_owner ADD MEMBER admin_user;
GO
EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'门禁管理系统管理员用户，拥有数据库内全部权限',
    @level0type = N'USER', @level0name = N'admin_user';
GO

CREATE USER operator_user FOR LOGIN operator_user;
GO
ALTER ROLE db_owner ADD MEMBER operator_user;
GO
EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'门禁系统运营用户，拥有数据库内全部权限',
    @level0type = N'USER', @level0name = N'operator_user';
GO

CREATE USER monitor_user FOR LOGIN monitor_user;
GO
ALTER ROLE db_owner ADD MEMBER monitor_user;
GO
EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'门禁系统运维用户，拥有数据库内全部权限',
    @level0type = N'USER', @level0name = N'monitor_user';
GO

CREATE USER integration_user FOR LOGIN integration_user;
GO
ALTER ROLE db_owner ADD MEMBER integration_user;
GO
EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'门禁系统集成用户，拥有数据库内全部权限',
    @level0type = N'USER', @level0name = N'integration_user';
GO

SELECT
    v.UserName             AS 用户名,
    v.RoleName             AS 角色,
    v.PermissionDescription AS 权限描述,
    v.InitialPassword      AS 初始密码
FROM (VALUES
    (N'admin_user',      N'db_owner', N'数据库内全部权限', N'123456'),
    (N'operator_user',   N'db_owner', N'数据库内全部权限', N'Operator@123'),
    (N'monitor_user',    N'db_owner', N'数据库内全部权限', N'Monitor@123'),
    (N'integration_user',N'db_owner', N'数据库内全部权限', N'Integration@123')
) AS v(UserName, RoleName, PermissionDescription, InitialPassword);
GO

SELECT
    DB_NAME() AS database_name,
    CASE WHEN OBJECT_ID(N'dbo.system_users', N'U') IS NULL THEN 0 ELSE 1 END AS system_users_table_present,
    CASE WHEN OBJECT_ID(N'dbo.attendance_gate_v2', N'U') IS NULL THEN 0 ELSE 1 END AS attendance_table_present,
    CASE WHEN OBJECT_ID(N'dbo.device_operation_retry_states', N'U') IS NULL THEN 0 ELSE 1 END AS retry_state_table_present;
GO
