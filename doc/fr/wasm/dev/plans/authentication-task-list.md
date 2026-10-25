# WebAssembly Authentication Task List

## 1. IndexedDB User Store
- [ ] Create a new IndexedDB database for user accounts
- [ ] Store `userId`, `username`, `passwordHash`, `salt`, `groupId`, and optional `twoFactorSecret`
- [ ] Seed the first user as administrator on first run
- [ ] Implement group store mapping `groupId` to `groupName`

## 2. Welcome and Login Screens
- [ ] Implement a welcome screen that appears on first launch to create the admin user
- [ ] Create a login screen that blocks access to the desktop until authentication
- [ ] Add logout capability returning to the login screen

## 3. Authentication Service
- [ ] Implement `AuthService` with `login`, `logout`, `getCurrent`, `getUserId`, `getUserGroup`, and `getGroups`
- [ ] Hash passwords using a salted algorithm (e.g., PBKDF2)
- [ ] Store and verify hashed passwords
- [ ] Provide 2FA support with TOTP (Google Authenticator)

## 4. User Management
- [ ] Allow admin to create, modify, and delete regular users
- [ ] Manage user rights based on group membership
- [ ] Provide each user with `/home/{UserName}` directory

## 5. Encryption Service
- [ ] Implement encryption and decryption for files and text using a user-derived key
- [ ] Integrate encryption with the file system and user directories

## 6. Testing and Validation
- [ ] Write unit tests for authentication flows
- [ ] Verify IndexedDB persistence of users and groups
- [ ] Test 2FA setup and validation
- [ ] Ensure unauthorized users cannot access the system
