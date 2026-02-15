# Login System Migration - Summary

## Changes Made

### 1. Removed Old `users` Table Dependencies
- ? `ValidateAdminAsync` now **only** checks `ua_credentials` table
- ? `ValidateClientAsync` now **only** checks `ui_credentials` table
- ? `GetUserIdByUsernameAsync` now **only** checks new credential tables
- ? Removed all methods that created/updated users in old `users` table

### 2. Updated `system_logs` Table Schema
- ? Changed `user_id` from `INTEGER` to `VARCHAR(20)` to support empID references
- ? Reordered columns to match new specification
- ? Migration handles conversion from old integer user_id to new VARCHAR(20)
- ? Added index on `user_id` for better query performance

### 3. Authentication Flow

#### Admin Login:
```
1. Check ua_credentials table
2. If table error or user not found ? Fallback to hardcoded (admin/admin123)
```

#### Instructor/Client Login:
```
1. Check ui_credentials table
2. If table error or user not found ? Fallback to hardcoded (student/student123 or client/client123)
```

### 4. Hardcoded Fallback Credentials

These work if the database tables don't exist or have errors:

**Admin:**
- Username: `admin`
- Password: `admin123`

**Student/Client:**
- Username: `student` Password: `student123`
- Username: `client` Password: `client123`

## Required Migration Steps

### Step 1: Run the Migration Script
```bash
psql -U postgres -d labserver -f credentials_migration.sql
```

This will:
- Create `ua_geninfo` and `ui_geninfo` tables
- Create `ua_credentials` and `ui_credentials` tables
- Update `system_logs` table schema (convert user_id to VARCHAR(20))
- Insert default admin (username: admin, password: admin123)

### Step 2: Add Your Admin/Instructor Accounts

**Add an Admin:**
```sql
-- 1. Add employee info
INSERT INTO ua_geninfo (empID, full_name, email) 
VALUES ('EMP001', 'John Admin', 'john@example.com');

-- 2. Add credentials
INSERT INTO ua_credentials (empID, username, password_hash, role) 
VALUES ('EMP001', 'johnadmin', '$2a$10$...bcrypt_hash...', 'ADMIN');
```

**Add an Instructor:**
```sql
-- 1. Add employee info
INSERT INTO ui_geninfo (empID, full_name, email) 
VALUES ('INS001', 'Jane Instructor', 'jane@example.com');

-- 2. Add credentials
INSERT INTO ui_credentials (empID, username, password_hash, role) 
VALUES ('INS001', 'janeinst', '$2a$10$...bcrypt_hash...', 'INSTRUCTOR');
```

### Step 3: Hash Passwords

To generate BCrypt hashes for passwords, you can use online tools or C#:
```csharp
var hash = BCrypt.Net.BCrypt.HashPassword("yourpassword");
```

## Database Schema

### ua_credentials (Admin Credentials)
```sql
id              SERIAL PRIMARY KEY
empID           VARCHAR(20) REFERENCES ua_geninfo(empID)
username        VARCHAR(50)
password_hash   VARCHAR(255) NOT NULL
is_temp_password BOOLEAN DEFAULT TRUE
created_at      TIMESTAMP DEFAULT CURRENT_TIMESTAMP
role            VARCHAR(50) DEFAULT 'ADMIN'
```

### ui_credentials (Instructor Credentials)
```sql
id              SERIAL PRIMARY KEY
empID           VARCHAR(20) REFERENCES ui_geninfo(empID)
username        VARCHAR(50)
password_hash   VARCHAR(255) NOT NULL
is_temp_password BOOLEAN DEFAULT TRUE
created_at      TIMESTAMP DEFAULT CURRENT_TIMESTAMP
role            VARCHAR(50) DEFAULT 'INSTRUCTOR'
```

### system_logs (Updated Schema)
```sql
id              SERIAL PRIMARY KEY
log_level       VARCHAR(10) NOT NULL
log_message     TEXT NOT NULL
action_type     VARCHAR(50)
user_id         VARCHAR(20)          -- Changed from INTEGER to VARCHAR(20)
computer_id     INT REFERENCES computers(id)
log_time        TIMESTAMP DEFAULT CURRENT_TIMESTAMP
```

## Testing

1. **Without database setup:**
   - Login with `admin/admin123` should work (hardcoded fallback)

2. **After running migration:**
   - Login with `admin/admin123` should work (from database)
   - Add your own admins/instructors and test

3. **Login methods supported:**
   - By `username`
   - By `empID`

4. **Logging:**
   - System logs now support VARCHAR user_id (can store empID directly)
   - Old integer user_id values are automatically migrated to VARCHAR

## Important Notes

- ?? The old `users` table is **NO LONGER USED** for authentication
- ?? Change the default admin password immediately after setup
- ?? `system_logs.user_id` is now VARCHAR(20) to support empID references
- ? Passwords are auto-upgraded to BCrypt if stored as plain text
- ? Both username and empID can be used for login
- ? Hardcoded credentials work as emergency fallback
- ? Migration script handles automatic conversion of old system_logs data
