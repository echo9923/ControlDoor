-- Stage 1-4 integration seed database for ControlDoor.
-- Intended for a disposable Docker SQL Server instance.
-- It creates the tables required by the first four stages and inserts one
-- disabled placeholder device so the service can start without touching hardware.

USE master;
GO

IF DB_ID(N'ruoyi-vue-pro') IS NULL
BEGIN
    CREATE DATABASE [ruoyi-vue-pro] COLLATE Chinese_PRC_CI_AS;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'door_user')
BEGIN
    CREATE LOGIN door_user
    WITH PASSWORD = N'change_me',
         DEFAULT_DATABASE = [ruoyi-vue-pro],
         CHECK_EXPIRATION = OFF,
         CHECK_POLICY = OFF;
END
ELSE
BEGIN
    ALTER LOGIN door_user
    WITH PASSWORD = N'change_me',
         DEFAULT_DATABASE = [ruoyi-vue-pro],
         CHECK_EXPIRATION = OFF,
         CHECK_POLICY = OFF;
END
GO

USE [ruoyi-vue-pro];
GO

IF USER_ID(N'door_user') IS NULL
BEGIN
    CREATE USER door_user FOR LOGIN door_user;
END
GO

ALTER ROLE db_owner ADD MEMBER door_user;
GO

IF OBJECT_ID(N'dbo.devices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.devices
    (
        device_id      INT           NOT NULL,
        device_name    NVARCHAR(255) NOT NULL,
        description    NVARCHAR(255) NULL,
        ip_address     NVARCHAR(20)  NOT NULL,
        port           NVARCHAR(5)   NOT NULL CONSTRAINT DF_devices_port DEFAULT N'8000',
        username       NVARCHAR(45)  NOT NULL CONSTRAINT DF_devices_username DEFAULT N'admin',
        [password]     NVARCHAR(255) NOT NULL,
        status         TINYINT       NOT NULL CONSTRAINT DF_devices_status DEFAULT 1,
        last_used_time DATETIME2(0)  NULL,
        created_at     DATETIME2(0)  NOT NULL CONSTRAINT DF_devices_created_at DEFAULT SYSDATETIME(),
        updated_at     DATETIME2(0)  NOT NULL CONSTRAINT DF_devices_updated_at DEFAULT SYSDATETIME(),
        CONSTRAINT PK_devices PRIMARY KEY (device_id)
    );

    CREATE INDEX idx_devices_ip_address ON dbo.devices(ip_address);
    CREATE INDEX idx_devices_status ON dbo.devices(status);
END
GO

IF OBJECT_ID(N'dbo.system_users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.system_users
    (
        id                    BIGINT        IDENTITY(1,1) NOT NULL,
        username              NVARCHAR(30)  NOT NULL,
        [password]            NVARCHAR(100) NOT NULL CONSTRAINT DF_system_users_password DEFAULT N'',
        nickname              NVARCHAR(30)  NOT NULL,
        remark                NVARCHAR(500) NULL,
        dept_id               BIGINT        NULL,
        post_ids              NVARCHAR(255) NULL,
        email                 NVARCHAR(50)  NULL CONSTRAINT DF_system_users_email DEFAULT N'',
        mobile                NVARCHAR(11)  NULL CONSTRAINT DF_system_users_mobile DEFAULT N'',
        sex                   TINYINT       NULL CONSTRAINT DF_system_users_sex DEFAULT 0,
        avatar                NVARCHAR(512) NULL CONSTRAINT DF_system_users_avatar DEFAULT N'',
        status                TINYINT       NOT NULL CONSTRAINT DF_system_users_status DEFAULT 0,
        access_permission     TINYINT       NOT NULL CONSTRAINT DF_system_users_access_permission DEFAULT 2,
        last_synced_level     TINYINT       NULL,
        permission_updated_at DATETIME2(3)  NULL,
        last_synced_at        DATETIME2(3)  NULL,
        login_ip              NVARCHAR(50)  NULL CONSTRAINT DF_system_users_login_ip DEFAULT N'',
        login_date            DATETIME2(3)  NULL,
        creator               NVARCHAR(64)  NULL CONSTRAINT DF_system_users_creator DEFAULT N'',
        create_time           DATETIME2(3)  NOT NULL CONSTRAINT DF_system_users_create_time DEFAULT SYSDATETIME(),
        updater               NVARCHAR(64)  NULL CONSTRAINT DF_system_users_updater DEFAULT N'',
        update_time           DATETIME2(3)  NOT NULL CONSTRAINT DF_system_users_update_time DEFAULT SYSDATETIME(),
        deleted               BIT           NOT NULL CONSTRAINT DF_system_users_deleted DEFAULT 0,
        tenant_id             BIGINT        NOT NULL CONSTRAINT DF_system_users_tenant_id DEFAULT 1,
        CONSTRAINT PK_system_users PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_system_users_username UNIQUE (username)
    );

    CREATE INDEX idx_system_users_username ON dbo.system_users(username);
    CREATE INDEX idx_system_users_status ON dbo.system_users(status);
    CREATE INDEX idx_system_users_permission ON dbo.system_users(access_permission);
    CREATE INDEX idx_system_users_deleted ON dbo.system_users(deleted);
END
GO

IF OBJECT_ID(N'dbo.attendance_gate_v2', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.attendance_gate_v2
    (
        auto_id        BIGINT IDENTITY(1,1) NOT NULL,
        id             BIGINT NOT NULL,
        username       NVARCHAR(30) NOT NULL,
        nickname       NVARCHAR(50) NULL,
        record_datetime DATETIME2(7) NOT NULL,
        record_date    DATE NOT NULL,
        record_time    TIME(7) NOT NULL,
        direction      TINYINT NOT NULL,
        device_name    NVARCHAR(100) NULL,
        device_sn      NVARCHAR(100) NULL,
        card_no        NVARCHAR(64) NULL,
        snapshot_path  NVARCHAR(255) NULL,
        raw_payload    VARCHAR(MAX) NULL,
        event_type     TINYINT NULL,
        process_status TINYINT NOT NULL,
        process_message NVARCHAR(255) NULL,
        processed_at   DATETIME2(7) NULL,
        creator        NVARCHAR(64) NULL,
        create_time    DATETIME2(7) NOT NULL,
        updater        NVARCHAR(64) NULL,
        update_time    DATETIME2(7) NOT NULL,
        deleted        VARCHAR(1) NOT NULL,
        tenant_id      BIGINT NOT NULL,
        CONSTRAINT PK_attendance_gate_v2 PRIMARY KEY CLUSTERED (auto_id),
        CONSTRAINT ux_gate_v2_id UNIQUE (id)
    );
END
GO

IF OBJECT_ID(N'dbo.device_operation_retry_states', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.device_operation_retry_states
    (
        id BIGINT IDENTITY(1,1) NOT NULL,
        device_id INT NOT NULL,
        employee_id NVARCHAR(64) NOT NULL,
        permission_level INT NULL,
        permission_pending BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_permission_pending DEFAULT 0,
        permission_sync_completion_blocked BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_blocked DEFAULT 1,
        person_payload NVARCHAR(MAX) NULL,
        person_pending BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_person_pending DEFAULT 0,
        face_payload NVARCHAR(MAX) NULL,
        face_pending BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_face_pending DEFAULT 0,
        delete_person_pending BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_delete_person DEFAULT 0,
        delete_face_pending BIT NOT NULL CONSTRAINT DF_device_operation_retry_states_delete_face DEFAULT 0,
        attempt_count INT NOT NULL CONSTRAINT DF_device_operation_retry_states_attempt_count DEFAULT 0,
        next_retry_at DATETIME2(0) NULL,
        last_error NVARCHAR(2000) NULL,
        last_attempt_at DATETIME2(0) NULL,
        exhausted_at DATETIME2(0) NULL,
        created_at DATETIME2(0) NOT NULL CONSTRAINT DF_device_operation_retry_states_created_at DEFAULT SYSDATETIME(),
        updated_at DATETIME2(0) NOT NULL CONSTRAINT DF_device_operation_retry_states_updated_at DEFAULT SYSDATETIME(),
        CONSTRAINT PK_device_operation_retry_states PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_device_operation_retry_states_device_employee UNIQUE (device_id, employee_id)
    );
END
GO

IF OBJECT_ID(N'dbo.face_event_checkpoint', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.face_event_checkpoint
    (
        DeviceIP      NVARCHAR(45) NOT NULL PRIMARY KEY,
        LastSerialNo  BIGINT       NOT NULL,
        LastEventTime DATETIME2(3) NOT NULL,
        UpdatedAt     DATETIME2(3) NOT NULL CONSTRAINT DF_face_event_checkpoint_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END
GO

IF EXISTS (SELECT 1 FROM dbo.devices WHERE device_id = 9001)
BEGIN
    UPDATE dbo.devices
    SET device_name = N'阶段1-4联调占位设备',
        description = N'默认禁用；真实设备接入前不要启用',
        ip_address = N'127.0.0.250',
        port = N'8000',
        username = N'admin',
        [password] = N'change_me',
        status = 0,
        updated_at = SYSDATETIME()
    WHERE device_id = 9001;
END
ELSE
BEGIN
    INSERT INTO dbo.devices
    (
        device_id,
        device_name,
        description,
        ip_address,
        port,
        username,
        [password],
        status,
        created_at,
        updated_at
    )
    VALUES
    (
        9001,
        N'阶段1-4联调占位设备',
        N'默认禁用；真实设备接入前不要启用',
        N'127.0.0.250',
        N'8000',
        N'admin',
        N'change_me',
        0,
        SYSDATETIME(),
        SYSDATETIME()
    );
END
GO

SELECT
    DB_NAME() AS database_name,
    COUNT(*) AS device_count,
    SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END) AS enabled_device_count
FROM dbo.devices;
GO
